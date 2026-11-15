# Hydro Haptic ESB 1.0.0

Plugin für HydroLink ab 0.17.3, zwei Vibrationsmotoren am Hydro-ESB-Node.

## Installation und Updates
Unter Einstellungen → Plugins das Release-ZIP hydro.haptic.esb-1.0.0.zip hinzufügen, HydroLink neu starten, Plugin freigeben und starten. Für automatischen Start „Mit HydroLink starten“ wählen. Die dauerhafte Ablage liegt unter %LOCALAPPDATA%/VRC HydroLink/Plugins und bleibt bei App-Updates erhalten. Die Plugin-Seite ist in der Plugin-Verwaltung erreichbar.

„Updates prüfen“ verwendet https://github.com/Hydroman-dot/VRC-HydroLink-Haptic-Plugin/releases/latest. Jedes Release enthält ein ZIP und die zugehörige .sha256-Datei. Neue Versionen werden nach Auswahl installiert, nicht ungefragt.

## Bedienung
Dongle wird automatisch erkannt, auch nach COM-Port-Wechsel. Manueller Test: links/rechts/beide bei30% für3s mit einmaliger Freigabe. STOP beendet die Ausgabe. VRChat-Modus: Freigabe und Intensität im Radialmenü; keine zusätzliche PC-Freigabe und keine0%-Voraussetzung. Nach Funkrückkehr wird bei gültigen frischen Avatarwerten automatisch wieder freigegeben. PC-STOP verlangt im Radialmenü aus/an.

Avatarparameter: HydroHaptic/Left und Right Float0..1, Enabled Bool, Heartbeat wechselnder Bool, Intensity Float0..1. Namen im Plugin anpassbar. Motorwert=Kontakt*Intensität*60%, Prüfstandsmaximum60%. Heartbeat muss innerhalb100ms wechseln. OSC wird über HydroLinks gemeinsamen Dienst genutzt.

Avatarvorschau arbeitet ohne Node. Funkanzeige zeigt empfangene eindeutige Pakete und Sequenzlücken, keine RSSI-Signalstärke; Lücken sind erst bei nächstem Paket zählbar. Alte Werte werden markiert.

## Firmware und Grenzen
Das Plugin-ZIP enthält keine Firmware und flasht keine Geräte. Drei-Sekunden-Abschaltung setzt die dazugehörige Node-/Dongle-Firmware voraus; die lokale Firmware entscheidet unabhängig. Getesteter Stand: Node0dBm/3s, DongleVersion7 mit Empfangsstatistik. Andere Firmware kann früher stoppen oder keine Statistik liefern.

Softwaretests für Eingänge, STOP, automatische Freigabe, UI-Erstellung und Protokollparser bestanden. Es handelt sich weiterhin um einen Prototyp; keine allgemeine Hardware- oder Langzeitgarantie. Der Unity-Wizard wird separat verteilt.

## 1.0.1 — zuverlässiger Startwert für die Intensität

Benötigt HydroLink 0.17.4. Ein nach dem Plugin-/Avatarwechsel unveränderter Intensitätsparameter wird einmalig aus einer frischen, zum aktuellen Avatar passenden OSCQuery-Abfrage übernommen, solange noch kein Live-Intensitätswert vorliegt. Kontakte, Aktivierung und Lebenszeichen bleiben ausschließlich live; der 100-ms-Lebenszeichen-Watchdog bleibt erhalten. Die Vorschau zeigt fehlende Parameter einzeln und unterscheidet den Intensitäts-Startwert von Live-OSC. 12 hardwarefreie Prüfungen decken die Initialisierung und ihre Grenzen ab.
