using System.IO;
using System.Globalization;
using System.IO.Ports;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HydroLink.Sdk;

namespace HydroHaptic;

public sealed class HapticPlugin : IHydroLinkPlugin
{
    public string Id => "hydro.haptic.esb";
    public string Name => "Hydro Haptic ESB (Prüfstand)";
    public int ApiVersion => 1;
    public void Configure(IPluginContext context)
    {
        var output = new HapticOutput(context);
        output.LoadParameters();
        context.Register(output);
        context.RegisterPage(new PluginPage(Name, () => output.CreatePage()));
    }
}

internal sealed class HapticOutput(IPluginContext context) : IComponent
{
    public string Id => "hydro.haptic.esb.output";
    public ComponentKind Kind => ComponentKind.Output;
    private readonly object gate = new();
    private string requestedPort = "*", currentPort = "", message = "Dongle wird automatisch gesucht · Ausgänge gesperrt";
    private bool armRequest, stopRequest = true, busMode, ready, active, dongleConnected;
    private int left, right;
    private bool avatarMode;
    private long testUntil;
    private bool testPending;
    private int testLeft, testRight;
    private readonly AvatarInput avatar = new();
    private readonly AvatarInput preview = new();
    private LinkStatistics? linkStatistics;
    private long linkUpdated;
    private long lastInput;
    private CancellationTokenSource? stop;
    private Task? worker;
    private readonly List<IDisposable> subscriptions = [];
    private static long Now => Environment.TickCount64;
    private void Reset(string reason)
    {
        lock (gate) { armRequest = false; active = false; left = right = 0; testUntil = 0; testPending = false; avatar.Disarm(); message = reason; }
    }
    private void RequestStop() { lock (gate) { stopRequest = true; armRequest = false; active = false; left = right = 0; testUntil = 0; testPending = false; avatar.Disarm(); } }
    internal void Connect(string port)
    {
        port = port.Trim().ToUpperInvariant();
        if (port.Length > 0 && port != "*" && (!port.StartsWith("COM", StringComparison.Ordinal) ||
            !int.TryParse(port.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < 1))
            throw new ArgumentException("Bitte einen gültigen COM-Anschluss auswählen.");
        lock (gate) { RequestStop(); ready = false; if (requestedPort != port || port.Length == 0) dongleConnected = false; requestedPort = port; message = port.Length == 0 ? "Getrennt · Ausgänge gesperrt" : "Verbindung wird hergestellt …"; }
    }
    internal (bool Ready, bool Active, int Left, int Right, string Message, bool DongleConnected, string Port) Snapshot()
    { lock (gate) return (ready, active, left, right, message, dongleConnected, currentPort); }
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (worker is not null) return Task.CompletedTask;
        stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        subscriptions.Add(context.Bus.Subscribe("hydro-haptic/input", e =>
        {
            lock (gate)
            {
                if (!busMode || !active) return;
                if (e.Value.ValueKind != System.Text.Json.JsonValueKind.Object ||
                    !e.Value.TryGetProperty("left", out var l) || !e.Value.TryGetProperty("right", out var r) ||
                    l.ValueKind != System.Text.Json.JsonValueKind.Number || r.ValueKind != System.Text.Json.JsonValueKind.Number ||
                    !l.TryGetInt32(out int lv) || !r.TryGetInt32(out int rv) || lv is < 0 or > 60 || rv is < 0 or > 60)
                { stopRequest = true; return; }
                left = lv; right = rv; lastInput = Now;
            }
        }));
        subscriptions.Add(context.Bus.Subscribe("osc/value/avatar/parameters", e =>
        {
            lock (gate) { preview.Receive(e.Topic, e.Value, Now); if (avatarMode && !avatar.Receive(e.Topic, e.Value, Now)) RequestStop(); }
        }));
        subscriptions.Add(context.Bus.Subscribe("osc/target-changed", _ => RequestStop()));
        subscriptions.Add(context.Bus.Subscribe("osc/query-lost", _ => RequestStop()));
        subscriptions.Add(context.Bus.Subscribe("routing/reset", _ => RequestStop()));
        subscriptions.Add(context.Bus.Subscribe("osc/avatar-id", _ => { lock (gate) { avatar.Reset(); preview.Reset(); RequestStop(); } }));
        worker = Task.Run(() => Run(stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }
    private async Task Run(CancellationToken cancellation)
    {
        SerialPort? serial = null;
        var line = new StringBuilder(180);
        uint boot = 0, generation = 0, sequence = 0;
        long lastNodeOnline = 0, nextAvatarArm = 0;
        long lastStatus = 0, lastPing = 0, retryAt = 0, armAt = 0, lastIteration = Now, openedAt = 0;
        int candidateIndex = 0;
        bool identified = false, awaitingArm = false, discard = false;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    if (Now - lastIteration >= 100)
                    {
                        serial?.DiscardInBuffer(); awaitingArm = false;
                        RequestStop(); Reset("Verarbeitung verzögert · erneut freigeben");
                        lastStatus = 0;
                    }
                    lastIteration = Now;
                    string port; lock (gate) port = requestedPort;
                    if (serial is not null && (port.Length == 0 || (port != "*" && serial.PortName != port)))
                    {
                        if (identified) serial.WriteLine("STOP"); serial.Dispose(); serial = null;
                        lock (gate) ready = dongleConnected = false;
                        identified = awaitingArm = false; lastStatus = 0;
                        Reset("Getrennt · erneut freigeben");
                    }
                    if (serial is null && port.Length > 0 && Now >= retryAt)
                    {
                        string selected = port;
                        if (port == "*")
                        {
                            var candidates = DongleDiscovery.Ports();
                            if (candidates.Length == 0) { retryAt = Now + 1000; Reset("Dongle wird gesucht · Ausgänge gesperrt"); await Task.Delay(25, cancellation); continue; }
                            selected = candidates[(candidateIndex++ & int.MaxValue) % candidates.Length];
                        }
                        identified = false;
                        lock (gate) linkStatistics = null;
                        serial = new SerialPort(selected, 115200) { DtrEnable = true, NewLine = "\n", ReadTimeout = 50, WriteTimeout = 50 };
                        serial.Open(); serial.DiscardInBuffer(); serial.WriteLine("PING");
                        openedAt = Now;
                        lock (gate) currentPort = selected;
                        identified = awaitingArm = false; lastStatus = 0; lastPing = Now; line.Clear(); discard = false;
                        Reset("Verbunden · Identifikation läuft");
                    }
                    if (serial is not null)
                    {
                        for (int count = 0; count < 512 && serial.BytesToRead > 0; count++)
                        {
                            int c = serial.ReadByte();
                            if (c == '\r') continue;
                            if (c != '\n')
                            {
                                if (c is < 32 or > 126 || line.Length >= 180) { discard = true; RequestStop(); }
                                else if (!discard) line.Append((char)c);
                                continue;
                            }
                            string response = line.ToString(); line.Clear();
                            if (discard) { discard = false; continue; }
                            if (response == "PONG HYDRO-ESB-2") { identified = true; lock (gate) dongleConnected = true; RequestStop(); }
                            if (!identified) continue;
                            if (LinkStatistics.TryParse(response, out var link))
                            { lock (gate) { linkStatistics = link; linkUpdated = Now; } }
                            if (response.StartsWith("ERR", StringComparison.Ordinal)) { awaitingArm = false; RequestStop(); Reset(response); }
                            if (TryStatus(response, out var s))
                            {
                                bool changed = boot != s.Boot || generation != s.Generation;
                                boot = s.Boot; generation = s.Generation; lastStatus = Now;
                                lock (gate)
                                {
                                    ready = identified && s.Online;
                                    if (s.Online) lastNodeOnline = Now;
                                    if (changed || (!ready && (!active || Now - lastNodeOnline >= 3000)) || (active && !s.Armed))
                                    { stopRequest = true; awaitingArm = false; Reset("Verbindung geändert · erneut freigeben"); }
                                    if (awaitingArm && s.Armed && s.NodeArmed)
                                    { awaitingArm = false; active = true; left = right = 0; lastInput = Now;
                                        if (testPending) { left = testLeft; right = testRight; testUntil = Now + 3000; testPending = false; } }
                                    message = ready ? (active ? $"Freigegeben · Node L {s.Left}% / R {s.Right}%" : "Dongle und Node verbunden · Ausgänge gesperrt") : (identified ? "Dongle verbunden · Node nicht erreichbar · gesperrt" : "Dongle wird identifiziert · gesperrt");
                                }
                                context.Bus.Publish("hydro-haptic/status", new { connected = ready, armed = active, nodeArmed = s.NodeArmed, left = s.Left, right = s.Right, boot, generation }, Id);
                            }
                        }
                        lock (gate)
                        {
                            if (testUntil != 0 && Now >= testUntil) RequestStop();
                            if (avatarMode && active)
                            {
                                if (avatar.TryRead(Now, out int avLeft, out int avRight)) { left = avLeft; right = avRight; }
                                else RequestStop();
                            }
                            if (Now - lastStatus >= 125 || (busMode && active && Now - lastInput >= 100) ||
                                (awaitingArm && Now - armAt >= 125))
                            { stopRequest = true; awaitingArm = false; ready = false; Reset("Status oder Eingang veraltet · gesperrt"); }
                            if (stopRequest && identified) { serial.WriteLine("STOP"); stopRequest = false; awaitingArm = false; }
                            if (avatarMode && !active && !awaitingArm && !stopRequest && ready && identified &&
                                Now - lastStatus < 125 && Now >= nextAvatarArm && avatar.CanAutoArm(Now))
                            { armRequest = true; nextAvatarArm = Now + 300; }
                            if (armRequest)
                            {
                                armRequest = false;
                                if (ready && identified && Now - lastStatus < 125 &&
                                    (!avatarMode || avatar.CanAutoArm(Now)))
                                { serial.WriteLine(FormattableString.Invariant($"ARM {boot} {generation}")); awaitingArm = true; armAt = Now; }
                            }
                            if (active) serial.WriteLine(FormattableString.Invariant($"SET {++sequence} {left} {right}"));
                        }
                        if (!identified && Now - lastPing >= 500) { serial.WriteLine("PING"); lastPing = Now; }
                        if ((!identified && Now - openedAt >= 900) ||
                            (identified && Now - Math.Max(lastStatus, openedAt) >= 1000))
                            throw new TimeoutException("Keine frische Dongle-Antwort; Suche wird fortgesetzt.");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or ArgumentException)
                {
                    serial?.Dispose(); serial = null; lock (gate) { ready = dongleConnected = false; currentPort = ""; } awaitingArm = false; retryAt = Now + 500;
                    Reset("Verbindungsfehler · Wiederverbindung bleibt gesperrt"); context.Log($"Hydro Haptic: {ex.Message}");
                    context.Bus.Publish("hydro-haptic/status", new { connected = false, armed = false }, Id);
                }
                await Task.Delay(25, cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (serial is not null) { try { if (serial.IsOpen && identified) serial.WriteLine("STOP"); } catch (Exception ex) { context.Log($"Hydro Haptic STOP: {ex.Message}"); } serial.Dispose(); }
            lock (gate) ready = dongleConnected = false; Reset("Gestoppt · Ausgänge gesperrt");
        }
    }
    internal readonly record struct Status(uint Boot, uint Generation, bool Online, bool Armed, bool NodeArmed, int Left, int Right);
    internal static bool TryStatus(string line, out Status status)
    {
        status = default; var p = line.Split(' ', StringSplitOptions.None);
        if (p.Length != 10 || p[0] != "STATUS" || p[1] != "2") return false;
        var n = new uint[8];
        for (int i = 0; i < 8; i++) if (!uint.TryParse(p[i + 2], NumberStyles.None, CultureInfo.InvariantCulture, out n[i])) return false;
        if (n[2] > 1 || n[3] > 1 || n[4] > 1 || n[5] > 1 || n[6] > 60 || n[7] > 60) return false;
        status = new(n[0], n[1], n[2] != 0, n[3] != 0, n[5] != 0, (int)n[6], (int)n[7]); return true;
    }
    internal void LoadParameters()
    {
        try { preview.Parameters = avatar.Parameters = AvatarParameters.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        { context.Log("Haptic-Parameternamen konnten nicht geladen werden: " + ex.Message); }
    }
    public object CreatePage()
    {
        RequestStop(); lock (gate) busMode = avatarMode = false;
        var root = new DockPanel { Margin = new Thickness(20), MaxWidth = 760 };
        TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 5) };
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "Hydro Haptic · zwei Motoren", FontSize = 22 });
        var connection = Text(""); header.Children.Add(connection);
        var stateLabel = Text(""); header.Children.Add(stateLabel);
        var receptionLabel = Text(""); header.Children.Add(receptionLabel);
        var receptionBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 12, Margin = new Thickness(0, 2, 0, 6) }; header.Children.Add(receptionBar);
        header.Children.Add(Text("Paketquote seit dem ersten empfangenen Paket dieses Node-Starts. Lücken werden beim nächsten Paket nachgezählt; keine Messung der Signalstärke."));
        var previewLabel = Text(""); header.Children.Add(previewLabel);
        var feedback = Text(""); header.Children.Add(feedback);
        Button AddButton(Panel parent, string caption, Action action)
        {
            var button = new Button { Content = caption, Margin = new Thickness(0, 4, 8, 4), Padding = new Thickness(10, 6, 10, 6) };
            button.Click += (_, _) => action(); parent.Children.Add(button); return button;
        }
        var actions = new WrapPanel(); header.Children.Add(actions);
        var arm = AddButton(actions, "Motoren freigeben · Start bei 0 %", () =>
        {
            lock (gate)
            {
                if (!ready || active) { feedback.Text = "Freigabe erst bei verbundenem Node möglich."; return; }
                if (avatarMode) { feedback.Text = "Freigabe wird automatisch über das VRChat-Radialmenü gesteuert."; return; }
                left = right = 0; armRequest = true; feedback.Text = "Freigabe angefordert …";
            }
        });
        AddButton(actions, "STOP · beide Motoren sperren", () => { lock (gate) { avatar.StopByPc(); RequestStop(); } feedback.Text = "STOP · in VRChat im Radialmenü aus- und wieder einschalten."; });
        var body = new StackPanel();
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var connectionPanel = new StackPanel();
        body.Children.Add(new Expander { Header = "Verbindung einstellen", Content = connectionPanel });
        var automatic = new CheckBox { Content = "Dongle automatisch suchen, auch nach COM-Port-Wechsel", IsChecked = true };
        connectionPanel.Children.Add(automatic);
        var port = new ComboBox { IsEditable = true, MinWidth = 180, IsEnabled = false };
        connectionPanel.Children.Add(Text("Manuelle Auswahl: USB-Anschluss des Dongles (nicht Node-Diagnose)")); connectionPanel.Children.Add(port);
        void RefreshPorts()
        {
            string current = port.Text;
            try { port.ItemsSource = SerialPort.GetPortNames().OrderBy(p => p.Length).ThenBy(p => p).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { feedback.Text = "Anschlüsse: " + ex.Message; }
            port.Text = current;
        }
        RefreshPorts();
        automatic.Checked += (_, _) => { port.IsEnabled = false; Connect("*"); feedback.Text = "Automatische Suche gestartet."; };
        automatic.Unchecked += (_, _) => { port.IsEnabled = true; Connect(""); feedback.Text = "Anschluss auswählen und Verbinden drücken."; };
        var connectActions = new WrapPanel(); connectionPanel.Children.Add(connectActions);
        AddButton(connectActions, "Liste aktualisieren", () => { RefreshPorts(); feedback.Text = "Liste der USB-Anschlüsse aktualisiert."; });
        AddButton(connectActions, "Verbinden / Suche starten", () =>
        {
            try
            {
                if (automatic.IsChecked != true && string.IsNullOrWhiteSpace(port.Text)) throw new ArgumentException("Bitte zuerst einen COM-Anschluss auswählen.");
                Connect(automatic.IsChecked == true ? "*" : port.Text); feedback.Text = "Verbindung angefordert · Motoren bleiben gesperrt.";
            }
            catch (ArgumentException ex) { RequestStop(); feedback.Text = ex.Message; }
        });
        AddButton(connectActions, "Trennen", () => { Connect(""); feedback.Text = "Getrennt; automatische Suche angehalten."; });
        body.Children.Add(Text("Betriebsart"));
        var mode = new ComboBox { ItemsSource = new[] { "Manueller Motortest", "HydroLink-Datenbus (erweitert)", "VRChat-Avatarparameter" }, SelectedIndex = 0 };
        body.Children.Add(mode);
        var explanation = Text(""); body.Children.Add(explanation);
        var manualPanel = new StackPanel(); body.Children.Add(manualPanel);
        var sliders = new List<Slider>(); var valueLabels = new List<TextBlock>();
        foreach (bool isLeft in new[] { true, false })
        {
            var valueLabel = Text(isLeft ? "Links · 0 %" : "Rechts · 0 %"); valueLabels.Add(valueLabel); manualPanel.Children.Add(valueLabel);
            var slider = new Slider { Minimum = 0, Maximum = 60, TickFrequency = 1, IsSnapToTickEnabled = true, IsEnabled = false };
            slider.ValueChanged += (_, _) => { lock (gate) if (active && testUntil == 0 && !busMode && !avatarMode) { if (isLeft) left = (int)slider.Value; else right = (int)slider.Value; } };
            sliders.Add(slider); manualPanel.Children.Add(slider);
        }
        var testActions = new WrapPanel(); manualPanel.Children.Add(testActions);
        var testButtons = new List<Button>();
        foreach (var test in new[] { ("Links testen · 30 % / 3 s", 30, 0), ("Rechts testen · 30 % / 3 s", 0, 30), ("Beide testen · 30 % / 3 s", 30, 30) })
            testButtons.Add(AddButton(testActions, test.Item1, () =>
            {
                lock (gate) if (ready && !busMode && !avatarMode && !testPending && testUntil == 0)
                {
                    testLeft = test.Item2; testRight = test.Item3;
                    if (active) { left = testLeft; right = testRight; testUntil = Now + 3000; }
                    else { left = right = 0; testPending = true; armRequest = true; }
                    feedback.Text = "Test angefordert: eigene Freigabe, 30 % für 3 Sekunden; danach gesperrt.";
                }
            }));
        manualPanel.Children.Add(Text("Testknöpfe fordern selbst eine einmalige Freigabe an: 30 % für 3 Sekunden ab bestätigter Freigabe, danach gesperrt. Bei Funkfehlern wird sofort abgebrochen. Für die Regler weiterhin oben freigeben; sie laufen bis STOP."));
        var avatarPanel = new StackPanel(); body.Children.Add(avatarPanel);
        AvatarParameters parameters; lock (gate) parameters = avatar.Parameters;
        var fields = new List<TextBox>();
        foreach (var entry in new[] { ("Links (Float 0–1)", parameters.Left), ("Rechts (Float 0–1)", parameters.Right),
            ("Aktivierung (Bool)", parameters.Enabled), ("Lebenszeichen (Bool-Wechsel)", parameters.Heartbeat), ("Intensität (Float 0–1)", parameters.Intensity) })
        {
            avatarPanel.Children.Add(Text(entry.Item1));
            var field = new TextBox { Text = entry.Item2 }; fields.Add(field); avatarPanel.Children.Add(field);
        }
        AddButton(avatarPanel, "Parameternamen speichern", () =>
        {
            RequestStop();
            try
            {
                var updated = new AvatarParameters { Left = fields[0].Text.Trim(), Right = fields[1].Text.Trim(), Enabled = fields[2].Text.Trim(), Heartbeat = fields[3].Text.Trim(), Intensity = fields[4].Text.Trim() };
                updated.Save(); lock (gate) { preview.Reset(); avatar.Reset(); preview.Parameters = avatar.Parameters = updated; }
                feedback.Text = "Parameternamen gespeichert · Motoren gesperrt.";
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { feedback.Text = ex.Message; }
        });
        avatarPanel.Children.Add(Text("Namen ohne /avatar/parameters/. Radialmenü steuert Freigabe und Intensität automatisch; wechselndes Lebenszeichen erforderlich. Keine 0-%-Voraussetzung. 1,0 entspricht 60 % Motorleistung."));
        void UpdateMode()
        {
            manualPanel.Visibility = mode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            avatarPanel.Visibility = mode.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
            explanation.Text = mode.SelectedIndex switch
            {
                0 => "Direkt testen: links und rechts über Regler oder kurze Testimpulse steuern. Kein VRChat nötig.",
                1 => "Für andere HydroLink-Module: eine Quelle muss beide Motorwerte unter hydro-haptic/input mindestens alle 50 ms liefern. Ohne Quelle stoppt die Ausgabe. Für normale Motortests den manuellen Modus wählen.",
                _ => "VRChat steuert links und rechts über OSC-Avatarparameter. Benötigt das später erstellte Unity-Asset mit Aktivierung und Lebenszeichen sowie laufenden OSC-Empfang in HydroLink."
            };
        }
        mode.SelectionChanged += (_, _) => { RequestStop(); lock (gate) { busMode = mode.SelectedIndex == 1; avatarMode = mode.SelectedIndex == 2; } feedback.Text = "Betriebsart geändert · Motoren gesperrt."; UpdateMode(); };
        UpdateMode();
        void RefreshState()
        {
            lock (gate)
            {
                if (linkStatistics is { Seen: true } stats)
                {
                    bool fresh = dongleConnected && ready && Now - linkUpdated < 1500 && stats.Age < 1000;
                    receptionLabel.Text = $"Funkempfang{(fresh ? "" : " · VERALTET / Verbindung fehlt")}: {100 - stats.LossPercent:0.0}% · Empfangen: {stats.Received:N0} · Fehlend: {stats.Missing:N0} ({stats.LossPercent:0.0}%)";
                    receptionBar.Value = 100 - stats.LossPercent; receptionBar.Opacity = fresh ? 1 : 0.3;
                }
                else { receptionLabel.Text = "Funkempfang: noch keine Messwerte (Dongle-Firmware mit Empfangsstatistik erforderlich)."; receptionBar.Value = 0; }
                var pv = preview.Preview();
                previewLabel.Text = pv is { } v ? $"Avatar-Vorschau {(Now - preview.PreviewUpdatedAt >= 1000 ? "(veraltet; letzte Werte)" : "(aktuell)")}: Links {v.Left:0}% / Rechts {v.Right:0}% · Motor-Soll mit Intensität: {v.MotorLeft:0}% / {v.MotorRight:0}% · keine Bestätigung der Motorausgabe" : "Avatar-Vorschau: warte auf Links-, Rechts- und Intensitätsparameter (kein Node erforderlich).";
                connection.Text = $"Dongle: {(dongleConnected ? "verbunden" : "nicht verbunden")}  ·  COM-Port: {(currentPort.Length > 0 ? currentPort : "noch nicht gefunden")}  ·  Node: {(ready ? "online" : "nicht erreichbar")}";
                bool avatarReady = !avatarMode || avatar.CanAutoArm(Now);
                arm.IsEnabled = !avatarMode && ready && !active && !armRequest && !testPending && avatarReady;
                stateLabel.Text = avatarMode ?
                    $"Radialmenü-Freigabe: {(avatar.DesiredEnabled ? "AN" : "AUS")} · " +
                    (avatar.StoppedByPc ? "PC-STOP aktiv: im Radialmenü aus/an schalten." :
                     !ready ? "Funkverbindung fehlt; Motoren spätestens nach 3 s ohne Befehl aus. Freigabewunsch bleibt erhalten." :
                     active ? $"Motorsteuerung aktiv: Links {left}% / Rechts {right}%." :
                     avatarReady ? "Automatische Freigabe wird hergestellt." : "Warte auf frische Avatarwerte und Lebenszeichen.") : active ? $"Motoren freigegeben · Links {left} % / Rechts {right} %" :
                    !ready ? "Motoren gesperrt · warte auf die Verbindung zum Node." :
                    !avatarReady ? "Motoren gesperrt · Avatar-Eingänge fehlen, sind veraltet oder nicht bei 0." : "Bereit · Testknöpfe geben einmalig frei; Regler benötigen Motorfreigabe.";
                foreach (var button in testButtons) button.IsEnabled = ready && !armRequest && !testPending && testUntil == 0 && !busMode && !avatarMode;
                for (int i = 0; i < sliders.Count; i++)
                {
                    sliders[i].IsEnabled = active && testUntil == 0 && !busMode && !avatarMode;
                    if (!active) sliders[i].Value = 0;
                    valueLabels[i].Text = (i == 0 ? "Links" : "Rechts") + $" · {(i == 0 ? left : right)} % (max. 60 %)";
                }
            }
        }
        var timer = new DispatcherTimer(DispatcherPriority.Background, root.Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => RefreshState();
        root.Loaded += (_, _) => { RefreshState(); timer.Start(); };
        root.Unloaded += (_, _) => { timer.Stop(); RequestStop(); };
        RefreshState(); return root;
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var subscription in subscriptions) subscription.Dispose(); subscriptions.Clear();
        if (stop is null) return;
        await stop.CancelAsync(); if (worker is not null) await worker;
        stop.Dispose(); stop = null; worker = null;
    }
}
