using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HydroHaptic;

internal sealed record AvatarParameters
{
    public string Left { get; init; } = "HydroHaptic/Left";
    public string Right { get; init; } = "HydroHaptic/Right";
    public string Enabled { get; init; } = "HydroHaptic/Enabled";
    public string Heartbeat { get; init; } = "HydroHaptic/Heartbeat";
    public string Intensity { get; init; } = "HydroHaptic/Intensity";
    internal void Validate()
    {
        var names = new[] { Left, Right, Enabled, Heartbeat, Intensity };
        if (names.Any(n => n is null || !Regex.IsMatch(n, @"\A[A-Za-z0-9_][A-Za-z0-9_/-]{0,95}\z")) ||
            names.Distinct(StringComparer.Ordinal).Count() != 5)
            throw new ArgumentException("Fünf unterschiedliche Parameternamen eingeben (Buchstaben, Ziffern, _, -, /; ohne OSC-Präfix).");
    }
    internal static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRC HydroLink", "hydro-haptic-parameters.json");
    internal static AvatarParameters Load()
    {
        if (!File.Exists(FilePath)) return new();
        var value = JsonSerializer.Deserialize<AvatarParameters>(File.ReadAllText(FilePath)) ?? throw new ArgumentException("Leere Parameterdatei.");
        value.Validate(); return value;
    }
    internal void Save()
    {
        Validate(); Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(FilePath + ".tmp", FilePath, true);
    }
}

// Called only under the output gate. Heartbeat edges renew the source lease;
// repeating an unchanged heartbeat or motor value cannot keep output alive.
internal sealed class AvatarInput
{
    internal AvatarParameters Parameters { get; set; } = new();
    private double left, right, intensity;
    private bool haveIntensity;
    private bool haveLeft, haveRight, enabled, haveHeartbeat, heartbeat, haveEdge;
    private long lastEdge;
    private bool stoppedByPc, sawDisabledAfterStop;
    internal bool IntensityFromQuery { get; private set; }
    internal string MissingPreviewParameters => string.Join(", ",new[]{haveLeft?null:"Links",haveRight?null:"Rechts",haveIntensity?null:"Intensität"}.Where(x=>x is not null));
    internal bool SeedIntensity(string topic,JsonElement payload,string currentAvatar,DateTimeOffset epoch)
    {
        if(haveIntensity || string.IsNullOrEmpty(currentAvatar) || topic!="osc/query-value/avatar/parameters/"+Parameters.Intensity || payload.ValueKind!=JsonValueKind.Object) return false;
        if(!payload.TryGetProperty("avatar",out var identity) || identity.ValueKind!=JsonValueKind.String || identity.GetString()!=currentAvatar ||
           !payload.TryGetProperty("queryStarted",out var time) || time.ValueKind!=JsonValueKind.String || !time.TryGetDateTimeOffset(out var started) ||
           started<epoch || DateTimeOffset.UtcNow-started>TimeSpan.FromSeconds(5) || started>DateTimeOffset.UtcNow ||
           !payload.TryGetProperty("value",out var value) || value.ValueKind!=JsonValueKind.Number || !value.TryGetDouble(out var v) || !double.IsFinite(v) || v<0 || v>1) return false;
        intensity=v; haveIntensity=true; IntensityFromQuery=true; return true;
    }
    internal bool DesiredEnabled => enabled;
    internal bool StoppedByPc => stoppedByPc;
    internal void StopByPc() { stoppedByPc = true; sawDisabledAfterStop = false; Disarm(); }
    internal bool CanAutoArm(long now) => !stoppedByPc && TryRead(now, out _, out _);
    internal long PreviewUpdatedAt { get; private set; }
    internal void Reset() { left = right = intensity = 0; haveIntensity = false; IntensityFromQuery=false; haveLeft = haveRight = enabled = haveHeartbeat = haveEdge = false; }
    internal void Disarm() { haveEdge = false; }
    internal bool Receive(string topic, JsonElement value, long now)
    {
        const string prefix = "osc/value/avatar/parameters/";
        if (!topic.StartsWith(prefix, StringComparison.Ordinal)) return true;
        string name = topic[prefix.Length..];
        if (name == Parameters.Left || name == Parameters.Right || name == Parameters.Intensity || name == Parameters.Heartbeat) PreviewUpdatedAt = now;
        if (name == Parameters.Left || name == Parameters.Right || name == Parameters.Intensity)
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double v) || !double.IsFinite(v) || v < 0 || v > 1)
            { Reset(); return false; }
            if (name == Parameters.Left) { left = v; haveLeft = true; }
            else if (name == Parameters.Right) { right = v; haveRight = true; }
            else { intensity = v; haveIntensity = true; IntensityFromQuery=false; }
        }
        else if (name == Parameters.Enabled || name == Parameters.Heartbeat)
        {
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { Reset(); return false; }
            bool v = value.GetBoolean();
            if (name == Parameters.Enabled) {
                enabled = v;
                if (!v) { if (stoppedByPc) sawDisabledAfterStop = true; Disarm(); return false; }
                if (stoppedByPc && sawDisabledAfterStop) { stoppedByPc = false; sawDisabledAfterStop = false; }
            }
            else
            {
                if (haveHeartbeat && heartbeat != v) { lastEdge = now; haveEdge = true; }
                heartbeat = v; haveHeartbeat = true;
            }
        }
        return true;
    }
    internal (double Left, double Right, double MotorLeft, double MotorRight)? Preview()
    {
        if (!haveLeft || !haveRight || !haveIntensity) return null;
        return (left * 100, right * 100, left * intensity * 60, right * intensity * 60);
    }
    internal bool TryRead(long now, out int l, out int r)
    {
        l = r = 0;
        if (stoppedByPc || !enabled || !haveIntensity || !haveLeft || !haveRight || !haveEdge || now - lastEdge >= 100) return false;
        l = (int)Math.Round(left * intensity * 60, MidpointRounding.AwayFromZero);
        r = (int)Math.Round(right * intensity * 60, MidpointRounding.AwayFromZero); return true;
    }
}
