# SonosControl – Senior-Analyse: Findings & Roadmap

Vollständige Code-Analyse (Code Fixes, Sanity, Clean Code, Performance, Logik, Doppelte Schichten).

## Erledigt (Fix-all vom 16.02.2025)

- **1.1 + 1.2:** UnitOfWork: Repos per DI (IHolidayRepo, ISonosConnectorRepo registriert); Property-Namen in SettingsRepo, SonosConnectorRepo, HolidayRepo umbenannt; alle Aufrufer + Tests angepasst.
- **2.1:** SetVolume delegiert an SetSpeakerVolume.
- **2.2:** GetCurrentTrackInfoAsync (string) entfernt.
- **2.3 + 2.4 + 2.5:** SonosConnectorRepo: ILogger eingebaut, alle Console.WriteLine durch _logger ersetzt; IsPlaying catch mit LogDebug; GetVolume null-sicher (volume?.Value ?? 0).
- **2.6:** SearchSpotifyTrackAsync prüft leeres items-Array.
- **2.7:** GetTrackProgressAsync mit XDocument + Log bei Fehler.
- **2.8:** GetAllSpeakersInGroup schreibt nicht mehr in Settings; nur noch Abgleich mit bereits gecachten UUIDs.
- **2.9:** GetPositionInfo-SOAP-Envelope als Konstante + Wiederverwendung in GetTrackInfoAsync/GetTrackProgressAsync.
- **4.1 + 4.2 + 4.3:** HolidayRepo: IsHoliday parst JSON (Array-Länge > 0); CancellationToken; Named Client "HolidayApi" in Program + Repo.
- **5.1 + 5.2 + 5.3:** SonosControlService: ILogger; GetPlayAction(schedule, settings) extrahiert; playAction immer gesetzt.
- **6.4 + 6.6:** SonosSettings.DefaultPlaceholderIp; IndexPage nutzt Konstante + Random.Shared; @implements IAsyncDisposable.
- **7.2:** SonosUrlHelper.NormalizeStationUrl in DAL; PlaybackMonitorService + IndexPage nutzen sie.
- **3.1:** GetSettings XML-Dokumentation (leeres Objekt bei Fehler).
- ConfigPage/UserManagement: _settings null-Check; Fire-and-forget mit _ = SaveSettings()/_ = AddLog(); UserManagement AddLog await.

---

## 1. Architektur & Dependency Injection

### 1.1 UnitOfWork bricht DI (kritisch)
- **Ort:** `SonosControl.DAL/Repos/UnitOfWork.cs`
- **Problem:** `HolidayRepo` und `SonosConnectorRepo` werden mit `new` erzeugt statt per DI. Das macht Unit-Tests schwer und verletzt das Prinzip „Program to interfaces“.
- **Empfehlung:** `IHolidayRepo` und `ISonosConnectorRepo` in `Program.cs` registrieren (z. B. Scoped), `UnitOfWork` nimmt beide per Konstruktorinjektion entgegen und gibt sie nur durch.

### 1.2 UnitOfWork-Property-Namen (Clean Code)
- **Ort:** `IUnitOfWork.cs`, `UnitOfWork.cs`, alle Aufrufer (`uow.ISettingsRepo`, `uow.ISonosConnectorRepo`, `uow.IHolidayRepo`)
- **Problem:** Properties heißen wie die Interfaces (`ISettingsRepo` statt z. B. `SettingsRepo`). Liest sich wie Typname, nicht wie Property.
- **Empfehlung:** Umbenennen in `SettingsRepo`, `SonosConnectorRepo`, `HolidayRepo` (oder `Settings`, `SonosConnector`, `Holidays`) und alle Referenzen anpassen.

---

## 2. DAL – SonosConnectorRepo

### 2.1 Doppelte Methoden: SetVolume vs SetSpeakerVolume
- **Ort:** `SonosConnectorRepo.cs` (ca. Zeilen 77–88)
- **Problem:** `SetVolume(ip, volume)` und `SetSpeakerVolume(ip, volume, cancellationToken)` machen dasselbe; eine ruft nur die andere nicht auf. Redundanz und Inkonsistenz (eine mit CancellationToken, eine ohne).
- **Empfehlung:** Eine Methode behalten (z. B. `SetVolume(string ip, int volume, CancellationToken cancellationToken = default)`), die andere darauf delegieren oder entfernen. Interface und Aufrufer anpassen.

### 2.2 Doppelte Schicht: GetCurrentTrackInfoAsync vs GetTrackInfoAsync/GetCurrentTrackAsync
- **Ort:** `SonosConnectorRepo.cs`: `GetCurrentTrackInfoAsync` (string), `GetTrackInfoAsync` (SonosTrackInfo?), `GetCurrentTrackAsync` (string)
- **Problem:** Zwei Wege, Track-Infos zu holen: alter String-Rückgabe (`GetCurrentTrackInfoAsync`) und neuer strukturierter Weg (`GetTrackInfoAsync` + `GetCurrentTrackAsync`). `GetCurrentTrackInfoAsync` wird im Projekt nicht genutzt (nur in Repo definiert).
- **Empfehlung:** `GetCurrentTrackInfoAsync` als veraltet markieren oder entfernen; überall nur `GetTrackInfoAsync`/`GetCurrentTrackAsync` nutzen. Interface bereinigen.

### 2.3 Console.WriteLine in Bibliothekscode
- **Ort:** `SonosConnectorRepo.cs` (mehrere Stellen: SetTuneInStationAsync, GetTrackInfoAsync, ClearQueue, CreateGroup, UngroupSpeaker, SendAvTransportCommand, SendSoapRequest, …)
- **Problem:** Logging über `Console.WriteLine` – nicht konfigurierbar, in Produktion unerwünscht, schwer testbar.
- **Empfehlung:** `ILogger<SonosConnectorRepo>` injizieren und alle `Console.WriteLine` durch passende Log-Level ersetzen (z. B. LogWarning/LogError bei Fehlern, LogDebug/LogInformation wo sinnvoll).

### 2.4 Leerer catch-Block
- **Ort:** `SonosConnectorRepo.IsPlaying` (ca. Zeilen 59–66)
- **Problem:** `catch { }` schluckt jede Exception; kein Log, kein Fehlerindikator.
- **Empfehlung:** Mindestens loggen und `false` zurückgeben, oder Exception weiterwerfen wenn Aufrufer Fehler brauchen.

### 2.5 GetVolume – mögliche NullReferenceException
- **Ort:** `SonosConnectorRepo.GetVolume`: `return volume.Value;`
- **Problem:** Wenn `GetVolumeAsync()` null zurückgibt, stürzt `volume.Value` ab.
- **Empfehlung:** Null-Check oder `volume?.Value ?? 0` (oder sinnvollen Default) mit Logging bei null.

### 2.6 SearchSpotifyTrackAsync – IndexOutOfRangeException
- **Ort:** `SonosConnectorRepo.SearchSpotifyTrackAsync`: Zugriff auf `items[0]` ohne Prüfung, ob das Array Elemente hat.
- **Problem:** Leere Suchergebnisse (`items: []`) führen zu IndexOutOfRangeException.
- **Empfehlung:** Vor Zugriff prüfen, z. B. `var items = doc.RootElement.GetProperty("tracks").GetProperty("items"); if (items.GetArrayLength() == 0) return null;`

### 2.7 GetTrackProgressAsync – XmlDocument/GetElementsByTagName
- **Ort:** `SonosConnectorRepo.GetTrackProgressAsync`: `doc.GetElementsByTagName("RelTime").Item(0)?.InnerText`
- **Problem:** `Item(0)` kann null sein; `?.InnerText` ist ok, aber wenn die XML-Struktur abweicht, können andere Nodes oder Fehler auftreten. Außerdem: generischer `catch` ohne Log.
- **Empfehlung:** Robuster parsen (z. B. mit XPath oder XDocument), bei Fehler loggen und (TimeSpan.Zero, TimeSpan.Zero) zurückgeben (wie bereits).

### 2.8 GetAllSpeakersInGroup – Seiteneffekt in „Get“
- **Ort:** `SonosConnectorRepo.GetAllSpeakersInGroup`
- **Problem:** Methode liest Gruppenzugehörigkeit, aktualisiert aber nebenbei fehlende Speaker-UUIDs in den Settings und ruft `_settingsRepo.WriteSettings(settings)` auf. Eine „Get“-Methode sollte keine Konfiguration schreiben.
- **Empfehlung:** UUID-Aktualisierung und Write in einen separaten Service/Workflow auslagern oder explizit benennen (z. B. `GetAllSpeakersInGroupAndPersistUuids`), oder UUID-Update nur an einer zentralen Stelle (z. B. beim Laden der Settings / Index-Seite) durchführen.

### 2.9 SOAP-Envelope-Duplikate
- **Ort:** Mehrere Methoden in `SonosConnectorRepo` (GetCurrentTrackInfoAsync, GetTrackInfoAsync, GetTrackProgressAsync, GetCurrentStationAsync, …)
- **Problem:** Fast identische SOAP-Envelopes mehrfach als String literal.
- **Empfehlung:** Gemeinsame Konstanten oder eine kleine Hilfsmethode (z. B. `BuildGetPositionInfoEnvelope()`) nutzen – weniger Fehler, bessere Wartbarkeit.

### 2.10 PreviousTrack – Pause + Delay + Previous
- **Ort:** `SonosConnectorRepo.PreviousTrack`
- **Problem:** Es wird zuerst pausiert, 500 ms gewartet, dann Previous gesendet. Ob das für alle Geräte/Szenarien sinnvoll ist, ist fraglich; z. B. bei „zurück zum Anfang“ reicht oft nur Previous.
- **Empfehlung:** Verhalten dokumentieren oder konfigurierbar machen; ggf. nur Previous senden und Pause weglassen, wenn Product so gewünscht.

---

## 3. DAL – SettingsRepo

### 3.1 GetSettings gibt „leeres“ Objekt bei Fehlern
- **Ort:** `SettingsRepo.GetSettings`: bei fehlender Datei oder JsonException wird `return new();` (leeres SonosSettings) verwendet.
- **Problem:** Unterschied „Datei leer/ungültig“ vs. „echtes leeres Konfigurationsobjekt“ geht verloren; Aufrufer können Fehler nicht unterscheiden.
- **Empfehlung:** Optional: bei Deserialize-Fehler eine spezifische Exception oder ein Result-Type (z. B. `(SonosSettings? settings, bool fromCache, string? error)`); mindestens dokumentieren, dass bei Fehler ein leeres Objekt zurückgegeben wird.

### 3.2 Newtonsoft.Json vs System.Text.Json
- **Ort:** `SettingsRepo` nutzt `Newtonsoft.Json`; Rest des .NET-Stacks oft `System.Text.Json`.
- **Problem:** Zwei Serializer im Projekt – unterschiedliches Verhalten (z. B. Namenspolitik, Null-Handling), mehr Abhängigkeiten.
- **Empfehlung:** Langfristig auf `System.Text.Json` umstellen und `DateOnlyJsonConverter` dafür implementieren; Migration und Tests durchziehen.

### 3.3 Caching: _cachingEnabled = false nach Watcher-Fehler
- **Ort:** `SettingsRepo`: wenn FileSystemWatcher nicht erstellt werden kann, wird Caching deaktiviert.
- **Problem:** Kein Fallback (z. B. zeitbasiertes Cache-Invalidieren); bei vielen Lesezugriffe mehr Disk I/O.
- **Empfehlung:** Optional: TTL-Cache oder kurzes Polling-Intervall, wenn Watcher fehlschlägt, damit nicht jeder Aufruf auf Disk geht.

---

## 4. DAL – HolidayRepo

### 4.1 IsHoliday – falsche Logik
- **Ort:** `HolidayRepo.IsHoliday()`: Rückgabe `responseBody.Length >= 3`.
- **Problem:** Es wird nicht geparst; z. B. `[]` (Länge 2) → false, `[{}]` (Länge 5) → true. Leeres Array „kein Feiertag“ wäre korrekt; „irgendwas mit Länge ≥ 3“ ist willkürlich.
- **Empfehlung:** JSON parsen (z. B. `JsonDocument`/`System.Text.Json`), Array-Länge prüfen: `length > 0` → Feiertag.

### 4.2 Kein CancellationToken, DateTime.Now
- **Ort:** `HolidayRepo.IsHoliday()`
- **Problem:** Kein CancellationToken; Nutzung von `DateTime.Now` (Testbarkeit, Zeitzone).
- **Empfehlung:** `IsHoliday(CancellationToken cancellationToken = default)`; für Datum `IDateTimeProvider`/`TimeProvider` injizieren oder `DateTime.UtcNow` und klare Zeitzonen-Dokumentation.

### 4.3 HttpClient ohne Namen
- **Ort:** `HolidayRepo`: `_httpClientFactory.CreateClient()` ohne Namen.
- **Problem:** Keine Möglichkeit, in `Program.cs` einen dedizierten Client (Timeout, User-Agent, Retry) für die Holidays-API zu konfigurieren.
- **Empfehlung:** Named Client (z. B. `"HolidayApi"`) und in `Program.cs` konfigurieren.

---

## 5. Web – SonosControlService

### 5.1 Console.WriteLine
- **Ort:** Mehrere Stellen in `SonosControlService.cs`
- **Empfehlung:** Durch `ILogger<SonosControlService>` ersetzen.

### 5.2 playAction theoretisch null
- **Ort:** `SonosControlService.StartSpeaker`: `Func<string, Task>? playAction = null;` – in allen Zweigen wird er gesetzt, aber der Compiler sieht das nicht.
- **Empfehlung:** Entweder am Ende `playAction ??= _ => Task.CompletedTask;` oder `throw new InvalidOperationException("No play action");` wenn doch ein Zweig fehlt; oder Struktur so umbauen, dass playAction nicht null sein kann (z. B. frühes return mit Default-Action).

### 5.3 Doppelte Logik Schedule vs Settings
- **Ort:** `StartSpeaker`: großer if/else für schedule vs. settings (PlayRandomSpotify, PlayRandomStation, …).
- **Problem:** Sehr ähnliche Zweige für Schedule und für Settings – DRY-Verletzung.
- **Empfehlung:** Gemeinsame Hilfsmethode (z. B. `GetPlayAction(DaySchedule? schedule, SonosSettings settings)`) die eine `Func<string, Task>` zurückgibt; Schedule-Werte haben Vorrang, sonst Settings.

---

## 6. Web – IndexPage.razor

### 6.1 IP_Adress Typo
- **Ort:** `SonosSettings.IP_Adress` (Model), überall in der App verwendet.
- **Problem:** Offensichtlicher Tippfehler („Adress“ statt „Address“).
- **Empfehlung:** Property in `IPAddress` umbenennen (oder `SelectedSpeakerIp`/ähnlich), Migration/Config/JSON-Kompatibilität beachten (Alias oder einmalige Migration der config.json).

### 6.2 IndexPage implementiert IAsyncDisposable, aber Basis nicht
- **Ort:** `IndexPage.razor`: `DisposeAsync` implementiert; Blazor-Komponenten erben von `ComponentBase`, das `IAsyncDisposable` nicht implementiert.
- **Problem:** `DisposeAsync` wird nur aufgerufen, wenn der Aufrufer explizit `await using` oder `IAsyncDisposable` nutzt; bei Blazor Server wird es je nach Lifecycle genutzt. Prüfen, ob Blazor Server `DisposeAsync` für Komponenten aufruft (ja, bei Disposal der Komponente).
- **Empfehlung:** So belassen; sicherstellen, dass Timer/CancellationTokenSources immer disposed werden (bereits der Fall). Optional explizit `@implements IAsyncDisposable` für Klarheit.

### 6.3 Timer/Callback und InvokeAsync
- **Ort:** `ConfigureQueueAutoRefreshTimer`: `_queueRefreshTimer = new Timer(_ => _ = InvokeAsync(...), ...)`
- **Problem:** Timer-Callback läuft außerhalb des Blazor-Sync-Kontexts; `InvokeAsync` ist korrekt. Kein offensichtlicher Bug, nur zur Kenntnis.
- **Empfehlung:** Beibehalten; ggf. in Docs festhalten, dass UI-Updates immer über `InvokeAsync` laufen.

### 6.4 Magic string „10.0.0.0“
- **Ort:** `OnAfterRenderAsync`: `!( _settings.IP_Adress is "10.0.0.0")`
- **Problem:** Platzhalter-IP als Magic String.
- **Empfehlung:** Konstante in `SonosSettings` oder Shared (z. B. `DefaultPlaceholderIp`) und hier verwenden.

### 6.5 Doppelte Aufrufe GetCurrentUserAsync
- **Ort:** Mehrere Stellen: `NotificationService.SendNotificationAsync(..., await GetCurrentUserAsync())`.
- **Problem:** Pro Aufruf wird erneut `GetAuthenticationStateAsync()` ausgeführt – bei vielen Notifications in einer Aktion unnötig.
- **Empfehlung:** User einmal zu Beginn der Aktion holen und als Parameter durchreichen, oder in SendNotificationAsync optional den Aufrufer-Context übergeben.

### 6.6 _random pro Komponente
- **Ort:** `private Random _random = new();` für ShuffleStation.
- **Problem:** Kein gravierender Bug; `Random.Shared` (ab .NET 6) wäre thread-sicher und einheitlich.
- **Empfehlung:** Auf `Random.Shared` umstellen (wie bereits in SonosControlService für GetRandomStationUrl etc.).

---

## 7. Web – PlaybackMonitorService

### 7.1 UpdateSessionDuration speichert nicht selbst
- **Ort:** `UpdateSessionDuration` ändert nur die Entity; `SaveChangesAsync` wird am Ende von `MonitorPlayback` aufgerufen.
- **Bewertung:** Korrekt, da gleicher Scope und am Ende `HasChanges()` + `SaveChangesAsync`. Kein Fix nötig; nur zur Klarheit in Kommentar erwähnen.

### 7.2 NormalizeStationUrl Duplikat
- **Ort:** `PlaybackMonitorService.NormalizeStationUrl` und `IndexPage`/`SonosConnectorRepo` haben ähnliche Logik (x-rincon-mp3radio entfernen, Trim).
- **Empfehlung:** Gemeinsame Hilfsmethode in DAL oder Shared (z. B. in einem `SonosUrlHelper` oder im Repo) und überall nutzen.

---

## 8. Performance & Async

### 8.1 SonosController pro Aufruf neu
- **Ort:** `SonosConnectorRepo`: in jeder Methode `new SonosControllerFactory().Create(ip)`.
- **Problem:** Pro Befehl eine neue Factory/Controller-Instanz; ByteDev.Sonos könnte intern HTTP nutzen – ggf. mehr Verbindungen als nötig.
- **Empfehlung:** Prüfen, ob Controller/Factory pro IP gecacht werden können (Lifecycle: Scoped pro Request oder mit TTL), um Verbindungen zu schonen. Abhängig von der ByteDev.Sonos-API.

### 8.2 Parallele Sonos-Aufrufe
- **Ort:** IndexPage: `UpdateSpeakerStatuses` macht pro Speaker `IsPlaying` + `GetCurrentStationAsync`; mehrere Speaker parallel mit `Task.WhenAll`. Gut.
- **Bewertung:** Bereits sinnvoll genutzt; keine Änderung nötig.

---

## 9. Sicherheit & Konfiguration

### 9.1 SOAP-Escape
- **Ort:** `SonosConnectorRepo`: Nutzung von `SecurityElement.Escape` für URIs/Metadaten in SOAP.
- **Bewertung:** Sinnvoll gegen Injection in XML; beibehalten.

### 9.2 Sensible Daten in Logs
- **Ort:** Überall wo URLs/Tokens geloggt werden könnten (z. B. bei Fehlern in SonosConnectorRepo).
- **Empfehlung:** Beim Umstieg auf ILogger: keine Webhook-URLs, Tokens oder IPs in Logs in Produktion (oder maskieren).

---

## 10. Tests

### 10.1 UnitOfWork mit new-Repos
- **Problem:** Unit-Tests für Services, die IUnitOfWork nutzen, können SonosConnectorRepo/HolidayRepo nicht mocken, wenn diese im UnitOfWork mit `new` erzeugt werden.
- **Empfehlung:** Siehe 1.1 – Repos per DI in UnitOfWork; dann in Tests UnitOfWork mocken oder echte Repo-Mocks injizieren.

### 10.2 HolidayRepo.IsHoliday
- **Problem:** Aktuell falsche Logik (Length >= 3); Tests würden falsches Verhalten absichern.
- **Empfehlung:** Nach Korrektur der Logik (JSON parsen) Unit-Tests mit Mock-HTTP und vorgegebenem JSON (leeres Array vs. ein Eintrag) hinzufügen.

---

## 11. Doppelte Schichten – Zusammenfassung

| Thema | Ort | Aktion |
|-------|-----|--------|
| SetVolume / SetSpeakerVolume | SonosConnectorRepo | Eine Methode, optional mit CancellationToken |
| GetCurrentTrackInfoAsync (string) vs GetTrackInfoAsync/GetCurrentTrackAsync | SonosConnectorRepo | Alte String-Methode entfernen/deprecated |
| SOAP-Envelopes | SonosConnectorRepo | Gemeinsame Konstanten/Hilfsmethode |
| Schedule vs Settings Play-Logik | SonosControlService | GetPlayAction(schedule, settings) extrahieren |
| NormalizeStationUrl | PlaybackMonitor, IndexPage, ggf. Repo | Zentrale Hilfsmethode (DAL/Shared) |
| Console.WriteLine | Repo + SonosControlService | Durch ILogger ersetzen |

---

## 12. Priorisierte To-dos (Kurzfassung)

1. **Kritisch:** UnitOfWork: Repos per DI, keine `new`-Instanzen.
2. **Kritisch:** `SearchSpotifyTrackAsync`: leeres `items`-Array prüfen (IndexOutOfRangeException vermeiden).
3. **Kritisch:** `HolidayRepo.IsHoliday`: JSON parsen, `length > 0` statt `Length >= 3`.
4. **Hoch:** `SonosConnectorRepo`: Console durch ILogger ersetzen; leeren catch in IsPlaying beheben; GetVolume null-sicher machen.
5. **Hoch:** SetVolume/SetSpeakerVolume zusammenführen; GetCurrentTrackInfoAsync entfernen oder deprecated.
6. **Mittel:** UnitOfWork-Property-Namen (ISettingsRepo → SettingsRepo o.ä.); IUnitOfWork-Interface anpassen und alle Aufrufer.
7. **Mittel:** GetAllSpeakersInGroup: Schreib-Seiteneffekt auslagern oder explizit machen.
8. **Niedrig:** IP_Adress → IPAddress (mit Migration); Magic-String 10.0.0.0; Random.Shared; NormalizeStationUrl zentralisieren.

---

*Erstellt als Senior-Analyse; Umsetzung schrittweise nach Priorität empfohlen.*
