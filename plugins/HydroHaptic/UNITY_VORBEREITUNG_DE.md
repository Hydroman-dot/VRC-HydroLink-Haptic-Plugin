> Aktualisierung: Das Headpat-Package 1.0.0 wurde inzwischen erstellt. Es ergänzt als fünften Parameter `HydroHaptic/Intensity` (Float 0–1). Die folgende frühere Planung ist historisch; für Einrichtung und aktuellen Vertrag die Headpat-Anleitung verwenden. Nach STOP bleiben Werte erhalten, ein neuer Heartbeatwechsel und bewusste Freigabe bleiben erforderlich. Avatarwechsel verwirft alle Werte.

# Hydro Haptic – Vorbereitung für das Unity-Asset

Stand: 10.11.2026. Das HydroLink-Plugin ist vorbereitet. Ein Unity-Package, Animator oder Prefab wurde auf Nutzerwunsch noch nicht erstellt. Die Verbindung mit einem echten Avatar ist noch nicht getestet.

## Testfunktion in HydroLink

Auf der Pluginseite den Eingang **Manueller Motortest** wählen. Dongle wird automatisch gesucht. Bei erreichbarem Node bewusst freigeben; Start bei null. Getrennte Regler links/rechts (0–60 %) und drei Testknöpfe stehen bereit: links, rechts oder beide bei 10 % für 0,5 Sekunden. Danach sperrt das Plugin beide Motoren wieder; vor dem nächsten Test erneut freigeben. STOP ist jederzeit möglich. 10 % können unterhalb der mechanischen Anlaufschwelle liegen; kein Drehen allein beweist keinen Fehler. Die motorfähige Node-Firmware wird benötigt.

## Vereinbarte Parameter

| Rolle | Vorgabename | Typ | Anfangswert | Bedeutung |
|---|---|---|---|---|
| Links | HydroHaptic/Left | Float | 0 | 0–1 ergibt 0–60 % links |
| Rechts | HydroHaptic/Right | Float | 0 | 0–1 ergibt 0–60 % rechts |
| Aktivierung | HydroHaptic/Enabled | Bool | false | false stoppt; true erlaubt nur die manuelle PC-Freigabe |
| Lebenszeichen | HydroHaptic/Heartbeat | Bool | false | Regelmäßig wechselndes false/true |

Namen können in HydroLink geändert und gespeichert werden, ohne Firmware neu zu bauen. Groß-/Kleinschreibung ist relevant. Eingabe ohne `/avatar/parameters/`; der Präfix wird automatisch ergänzt. Vier eindeutige Namen; erlaubt sind Buchstaben, Ziffern, Unterstrich, Bindestrich und Schrägstrich. Speichern stoppt die Ausgänge. Speicherung: `%LOCALAPPDATA%/VRC HydroLink/hydro-haptic-parameters.json`. Modus und Motorfreigabe werden absichtlich nicht wiederhergestellt.

Die beigefügte `unity-parameter-contract.json` beschreibt die Vorgaben für den späteren Asset-Generator; sie ist kein Unity-Asset und wird nicht als VRChat-OSC-Konfiguration importiert. Bei umbenannten Parametern beim späteren Erstellen die gespeicherte Parameterdatei als Namensquelle verwenden.

## Anforderungen an das spätere Asset

- Vier lokale Parameter mit den angegebenen Typen und Anfangswerten; nicht speichern und nicht über das Netzwerk synchronisieren. OSC-Ausgabe muss für alle vier vorhanden sein. Sensor-/Contact-Zuordnung und Menü werden erst in Unity erstellt.
- Lebenszeichen-Ziel: alle 40 ms den Bool wechseln; bei 100 ms ohne beobachteten Wechsel sperrt HydroLink. Ein wiederholt identischer Wert verlängert die Frist nicht. Diese strenge Vorgabe muss im späteren Avatar bei realer Bildrate/OSC-Ausgabe getestet werden.
- Nach Avatarwechsel, Moduswechsel, STOP oder Verbindungsreset muss HydroLink beide Motorwerte, Aktivierung und mindestens einen Lebenszeichenwechsel erneut empfangen. Initialisierung im Asset muss diese Übertragung sicherstellen, auch wenn Werte vorher bereits null waren. Nur gecachte oder unveränderte Animator-Defaults reichen als Nachweis nicht.
- Freigabe auf dem PC erst mit beiden Eingangswerten null, Enabled=true und frischem Lebenszeichen. Enabled ist keine automatische Motorfreigabe. Danach kann das Asset beide Werte unabhängig steuern. Werte außerhalb 0–1, falsche Typen und Enabled=false sperren.
- Konstante Motorwerte bleiben zwischen Änderungen gültig, solange das Lebenszeichen wechselt. Das Lebenszeichen muss aus der laufenden Avatarlogik stammen; kein PC-Timer darf einen ausgefallenen Avatar durch Wiederholen alter Werte verdecken.
- Nach Sperre keine automatische Wiederfreigabe. Die lokale Node-Abschaltung bleibt zusätzlich aktiv.

HydroLinks vorhandener OSC-Empfang veröffentlicht die Werte intern unter `osc/value/avatar/parameters/<Name>`; genau diese Ereignisse nutzt das Plugin. Keine zusätzlichen UDP-Anschlüsse oder Änderungen an vorhandenen Intiface-Routen nötig. HydroLinks OSC-Modul muss laufen. Der bisherige Datenbusmodus bleibt separat verfügbar.

## Nächste Unity-Sitzung

Gespeicherte Namen übernehmen, Parameterassets/FX-Animator/Heartbeat und Menü bzw. Sensoren erzeugen, in den Avatar integrieren und Initialübertragung prüfen. Anschließend reale Tests: Nullfreigabe, beide Seiten, unveränderte Werte mit laufendem Lebenszeichen, Lebenszeichenstillstand, Avatarwechsel, STOP, PC-/USB-/Funkunterbrechung und Wiederanlaufsperre. Erst danach das fertige importierbare Unity-Package exportieren. Der bisherige Stand ist noch kein Nachweis, dass ein beliebiger Avatar allein durch Import sofort funktioniert.

## Prüfung dieses Pluginstands

Release-Build bestanden; Tests für Skalierung, fehlende Eingänge, umbenannte Parameter, Lebenszeichenwechsel und exakte Ablaufgrenze, unveränderte Lebenszeichen, ungültige Werte und Deaktivierung bestanden. Bestehende Parser-, PluginLoader-, WPF-Seiten- und Lebenszyklustests bestanden. Keine Motorbefehle für diese Prüfung; keine neue Hardware- oder Unity-Abnahme.

Quellen für die spätere Unity-Integration: [VRChat OSC-Avatarparameter](https://docs.vrchat.com/docs/osc-avatar-parameters), [Animatorparameter](https://creators.vrchat.com/avatars/animator-parameters/), [Parameter Driver](https://creators.vrchat.com/avatars/state-behaviors/). Die vier Namen und Zeitgrenzen oben sind unsere Projektschnittstelle.
