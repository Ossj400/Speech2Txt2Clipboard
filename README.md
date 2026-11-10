# SpeechToTxt — dyktowanie (Sherpa) + transkrypcja plików z mówcami (MOSS)

Aplikacja WinUI 3 (.NET 10) z dwoma trybami rozpoznawania mowy:

| Tryb | Silnik | Zastosowanie | Język |
|------|--------|--------------|-------|
| **Na żywo z mikrofonu** | Sherpa-ONNX (streaming, CPU) | dyktowanie słowo-po-słowie | angielski |
| **Transkrypcja pliku** | MOSS-Transcribe-Diarize (batch, GPU) | całe nagranie + kto mówił + znaczniki czasu | polski i 50+ języków |

To dwa różne tryby — MOSS **nie** działa na żywo; bierze cały plik naraz (do 90 min).

---

## Wymagania trybu MOSS

- Karta NVIDIA (testowane na RTX 5060 Ti, 16 GB VRAM)
- Zainstalowany model w `Z:\models\MOSS-Transcribe-Diarize`
  (venv `.venv` z PyTorch cu128, wagi w `Z:\models\hf-cache`)

Serwer modelu (FastAPI) nasłuchuje na `http://127.0.0.1:8760`.

---

## Użycie — tryb MOSS (transkrypcja pliku)

1. Uruchom aplikację (konfiguracja **x64** w Visual Studio).
2. Kliknij **„Wybierz plik audio/wideo"** i wskaż nagranie.
3. Jeśli serwer MOSS nie działa — aplikacja **uruchomi go sama** (`serve_moss.bat`)
   i poczeka aż model się załaduje (pasek „Ladowanie modelu... (Ns)”).
4. Wynik pojawia się z podziałem na mówców:
   ```
   [00:05] S01: Witam wszystkich na dzisiejszym spotkaniu...
   [00:10] S02: Świetnie, model działa lokalnie...
   ```
5. **„Kopiuj transkrypcję”** — całość do schowka.

> Pierwsza transkrypcja po starcie = ~10–40 s (ładowanie modelu na zimno).
> Każda kolejna = kilka sekund (model zostaje w pamięci GPU).

### Serwer ręcznie (opcjonalnie)

Dwuklik na `Z:\models\MOSS-Transcribe-Diarize\serve_moss.bat`.
Poczekaj na komunikat `Model zaladowany`.

Przy normalnym zamknięciu okna aplikacja zatrzymuje serwer, który sama uruchomiła,
wraz z jego procesami potomnymi. Serwer uruchomiony wcześniej ręcznie pozostaje
uruchomiony. Serwer uruchomiony przez aplikację jest również zatrzymywany przez
Windows po wymuszonym zakończeniu aplikacji, np. po zatrzymaniu debugowania.

---

## Budowanie

WinUI wymaga jawnej platformy (nie AnyCPU):

```bash
dotnet build SpeechToTxtWInUI.csproj -c Debug -p:Platform=x64 -r win-x64 --self-contained
```

W konfiguracji Release przycinanie bibliotek (`PublishTrimmed`) i kompilacja
ReadyToRun (`PublishReadyToRun`) są wyłączone, aby umożliwić publikację także
bez dołączania środowiska .NET (framework-dependent).
Na komputerze docelowym potrzebne są wtedy odpowiednie środowiska .NET i Windows
App SDK. Serwer MOSS i jego model nadal wymagają osobnej instalacji.

---

## Struktura integracji MOSS

- `SpeechToTextAI/MossTranscriptionService.cs` — klient HTTP:
  - `EnsureServerRunningAsync(...)` — auto-start serwera + oczekiwanie na `/health`
  - `TranscribeFileAsync(path)` — POST pliku, zwraca segmenty (mówca/czas/tekst)
  - `FormatReadable(result)` — formatowanie do czytelnego tekstu
- `MainWindow.xaml` / `.cs` — przyciski `BtnMossFile` / `BtnMossCopy`

Ścieżka do serwera: właściwość `ServerBatPath` w `MossTranscriptionService.cs`
(domyślnie `Z:\models\MOSS-Transcribe-Diarize\serve_moss.bat`).
Zmień ją, jeśli przeniesiesz model.

### Skrypty po stronie modelu (`Z:\models\MOSS-Transcribe-Diarize`)

| Plik | Rola |
|------|------|
| `serve_moss.bat` / `serve_moss.py` | serwer HTTP (model w pamięci) |
| `transcribe_json.py` | jednorazowa transkrypcja pliku → JSON na stdout |
| `test_transcribe.py` | test z wypisaniem segmentów |
