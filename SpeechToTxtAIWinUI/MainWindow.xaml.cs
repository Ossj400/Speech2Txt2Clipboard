using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using WinRT.Interop;
using System.Media;
using SpeechToTextAI;
using Microsoft.UI;
using Microsoft.UI.Windowing;

namespace SpeechToTxtAI
{
    /// <summary>
    /// Glowny silnik: MOSS (polski, rozpoznaje mowcow). Model laduje sie przy starcie.
    /// Nasluch mikrofonu z auto-cieciem na pauzach: mowisz -> pauza -> po ~1-2s
    /// pojawia sie zdanie po polsku. MOSS dziala wsadowo, wiec nie da sie "litera po literze".
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private readonly MossTranscriptionService _mossService = new("http://127.0.0.1:8760");
        private readonly CancellationTokenSource _windowLifetime = new();
        private readonly AppSettings _settings = AppSettings.Load();
        private bool _closing;
        private bool _settingsLoaded;
        private WaveInEvent? _waveIn;
        private bool _isListening;
        private bool _serverReady;
        private string _fullTranscript = "";

        // Bufor biezacej wypowiedzi + detekcja ciszy (VAD po energii sygnalu)
        private readonly List<float> _utterance = new();
        private readonly Queue<float[]> _preBuffer = new();   // ostatnie porcje przed mowa
        private int _silenceMs;
        private bool _hasSpeech;
        private volatile bool _flushing;

        private const int SampleRate = 16000;
        private const int BitsPerSample = 16;
        private const int Channels = 1;

        // Progi VAD (SilenceFlushMs sterowany suwakiem, reszta stala)
        private const float SilenceRms = 0.012f;   // ponizej = cisza
        private int _silenceFlushMs = 1200;       // pauza konczaca wypowiedz (z ustawien)
        private const int MinUtteranceMs = 800;    // krotsze = szum/urwane, pomijamy
        private const int MaxUtteranceMs = 30000;  // twardy limit dlugosci fragmentu
        private const int PreBufferChunks = 1;     // ~100ms audio SPRZED mowy (minimalnie)

        #region Always On Top P/Invoke

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;

        #endregion

        public MainWindow()
        {
            InitializeComponent();

            var appWindow = GetAppWindow();
            if (appWindow != null)
            {
                appWindow.Resize(new Windows.Graphics.SizeInt32(380, 680));
                if (appWindow.Presenter is OverlappedPresenter presenter)
                {
                    // Zwykly systemowy pasek tytulu -> okno mozna przesuwac,
                    // minimalizowac i ZAMKNAC (przycisk X dziala).
                    // NIE uzywamy ExtendsContentIntoTitleBar (konflikt z systemowym
                    // paskiem powodowal "ducha" - okno zyje ale nie da sie go zamknac).
                    presenter.IsResizable = false;
                    presenter.IsMinimizable = true;
                    presenter.IsMaximizable = false;
                    presenter.SetBorderAndTitleBar(true, true);
                }
            }

            this.Activated += MainWindow_Activated;
            this.Closed += MainWindow_Closed;

            // Wczytaj zapisane ustawienia do kontrolek
            _silenceFlushMs = _settings.SilenceFlushMs;
            SliderSilence.Value = _silenceFlushMs;
            TxtSilenceLabel.Text = $"Czas pauzy: {_silenceFlushMs} ms";
            ChkAutoCopy.IsChecked = _settings.AutoCopy;
            _settingsLoaded = true;   // dopiero teraz handlery moga zapisywac

            // MOSS laduje sie od razu przy starcie aplikacji
            InitializeMossAsync();
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            SetAlwaysOnTop();
            this.Activated -= MainWindow_Activated;
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            _closing = true;
            _windowLifetime.Cancel();
            try
            {
                if (_isListening) StopListening(flushRemaining: false);
            }
            finally
            {
                _mossService.Dispose();
            }
        }

        private void SetAlwaysOnTop()
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
        }

        private Microsoft.UI.Windowing.AppWindow? GetAppWindow()
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            return Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        }

        /// <summary>
        /// Auto-start serwera MOSS + zaladowanie modelu przy uruchomieniu apki.
        /// </summary>
        private async void InitializeMossAsync()
        {
            BtnRecord.IsEnabled = false;
            TxtStatus.Text = "Ladowanie modelu MOSS...";
            try
            {
                _serverReady = await _mossService.EnsureServerRunningAsync(
                    progress: msg => DispatcherQueue.TryEnqueue(() => { if (!_closing) TxtStatus.Text = msg; }),
                    maxWaitSeconds: 180,
                    ct: _windowLifetime.Token);

                if (_closing) return;

                if (_serverReady)
                {
                    TxtStatus.Text = "Słucham... (mów i zrób pauzę)";
                    // Auto-start nasluchu zaraz po zaladowaniu modelu.
                    _ = StartListeningAsync();
                }
                else
                {
                    TxtStatus.Text = "Serwer MOSS niedostepny";
                    TxtResult.Text =
                        "Nie udalo sie uruchomic serwera.\n" +
                        "Sprawdz: Z:\\models\\MOSS-Transcribe-Diarize\\serve_moss.bat";
                }
            }
            catch (OperationCanceledException) when (_closing)
            {
            }
            catch (Exception ex)
            {
                if (_closing) return;
                TxtStatus.Text = "Blad inicjalizacji";
                TxtResult.Text = ex.Message;
            }
        }

        private void BtnRecord_Click(object sender, RoutedEventArgs e)
        {
            if (_isListening) StopListening();
            else _ = StartListeningAsync();
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(_fullTranscript))
            {
                CopyToClipboard(_fullTranscript);
                TxtStatus.Text = "Skopiowano!";
            }
        }

        private async Task StartListeningAsync()
        {
            if (!_serverReady) return;

            // Szybki check czy serwer faktycznie zyje (moze zginal od ostatniego razu).
            // NIE uzywamy .GetAwaiter().GetResult() - to blokowaloby watek UI i okno
            // by zamarzlo przy wolnym serwerze.
            try
            {
                if (!await _mossService.IsServerReadyAsync(_windowLifetime.Token))
                {
                    TxtStatus.Text = "Serwer nie odpowiada - probuje wstawic...";
                    BtnRecord.IsEnabled = false;
                    _serverReady = await _mossService.EnsureServerRunningAsync(maxWaitSeconds: 60, ct: _windowLifetime.Token);
                    if (_closing) return;
                    BtnRecord.IsEnabled = true;
                    if (!_serverReady)
                    {
                        TxtStatus.Text = "Serwer MOSS niedostepny";
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (_closing)
            {
                return;
            }

            if (_closing) return;

            _isListening = true;
            BtnRecord.IsEnabled = true;   // pozwol zatrzymac (klikniecie = stop)
            _fullTranscript = "";
            _utterance.Clear();
            _silenceMs = 0;
            _hasSpeech = false;
            TxtResult.Text = "";
            BtnCopy.Visibility = Visibility.Collapsed;

            IconMic.Visibility = Visibility.Collapsed;
            IconStop.Visibility = Visibility.Visible;
            TxtStatus.Text = "Słucham... (mow i zrob pauze)";

            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(SampleRate, BitsPerSample, Channels),
                BufferMilliseconds = 100
            };
            _waveIn.DataAvailable += OnAudioDataAvailable;
            _waveIn.StartRecording();
        }

        private void StopListening(bool flushRemaining = true)
        {
            _isListening = false;
            IconMic.Visibility = Visibility.Visible;
            IconStop.Visibility = Visibility.Collapsed;
            TxtStatus.Text = "Zatrzymano";

            _waveIn?.StopRecording();

            // Wyslij resztki jesli byla mowa
            if (flushRemaining && _hasSpeech && _utterance.Count > MinUtteranceMs * SampleRate / 1000)
                _ = FlushUtteranceAsync(_utterance.ToArray());

            CleanupRecording();

            if (!flushRemaining) return;

            DispatcherQueue.TryEnqueue(() =>
            {
                if (!string.IsNullOrWhiteSpace(_fullTranscript))
                {
                    CopyToClipboard(_fullTranscript);
                    BtnCopy.Visibility = Visibility.Visible;
                }
            });
        }

        /// <summary>
        /// Kazda porcja audio (~100ms): mierzymy energie, zbieramy wypowiedz,
        /// a po wykryciu ciszy wysylamy zebrany fragment do MOSS.
        /// </summary>
        private void OnAudioDataAvailable(object? sender, WaveInEventArgs args)
        {
            if (!_isListening || args.BytesRecorded == 0) return;

            int n = args.BytesRecorded / 2;
            var samples = new float[n];
            double sumSq = 0;
            for (int i = 0; i < n; i++)
            {
                short s16 = BitConverter.ToInt16(args.Buffer, i * 2);
                float s = s16 / 32768f;
                samples[i] = s;
                sumSq += s * s;
            }
            float rms = (float)Math.Sqrt(sumSq / Math.Max(1, n));
            int chunkMs = n * 1000 / SampleRate;

            bool voiced = rms >= SilenceRms;

            if (voiced)
            {
                // Mowa: najpierw dolacz porcje ciszy sprzed (pre-buffer - nie ucinaj poczatku)
                if (!_hasSpeech)
                {
                    while (_preBuffer.Count > 0)
                        _utterance.AddRange(_preBuffer.Dequeue());
                }
                _hasSpeech = true;
                _silenceMs = 0;
                _utterance.AddRange(samples);
                _preBuffer.Clear();   // juz wypchniete
            }
            else
            {
                // Cisza: trzymaj w pre-bufferze (max PreBufferChunks)
                _preBuffer.Enqueue(samples);
                while (_preBuffer.Count > PreBufferChunks)
                    _preBuffer.Dequeue();

                if (_hasSpeech)
                {
                    // cisza po mowie - dorzucamy (naturalne wybrzmienie) i liczymy pauze
                    _utterance.AddRange(samples);
                    _silenceMs += chunkMs;
                }
            }

            int utterMs = _utterance.Count * 1000 / SampleRate;

            // Warunek wyslania: pauza po mowie, albo przekroczony limit dlugosci
            bool pauseDone = _hasSpeech && _silenceMs >= _silenceFlushMs;
            bool tooLong = utterMs >= MaxUtteranceMs;

            if ((pauseDone || tooLong) && !_flushing && utterMs >= MinUtteranceMs)
            {
                var chunk = _utterance.ToArray();
                _utterance.Clear();
                _silenceMs = 0;
                _hasSpeech = false;
                _ = FlushUtteranceAsync(chunk);
            }
        }

        /// <summary>
        /// Wysyla zebrany fragment mowy do MOSS i dopisuje wynik na ekranie.
        /// </summary>
        private async Task FlushUtteranceAsync(float[] samples)
        {
            _flushing = true;
            try
            {
                DispatcherQueue.TryEnqueue(() => TxtStatus.Text = "Rozpoznaje...");
                var result = await _mossService.TranscribeSamplesAsync(samples, SampleRate, ct: _windowLifetime.Token);

                if (_closing) return;

                if (result.Ok && result.Segments.Count > 0)
                {
                    var text = MossTranscriptionService.FormatReadable(result).Trim();
                    if (!string.IsNullOrEmpty(text))
                    {
                        // Komenda glosowa: "wyczyść [tekst]" -> czysc transkrypcje i schowek.
                        if (text.Contains("wyczyść", StringComparison.OrdinalIgnoreCase))
                        {
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                ClearTranscript();
                                TxtStatus.Text = _isListening ? "Słucham... (wyczyszczone)" : "Zatrzymano";
                            });
                        }
                        else if (text.Contains("wklej", StringComparison.OrdinalIgnoreCase))
                        {
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                if (string.IsNullOrWhiteSpace(_fullTranscript))
                                {
                                    TxtStatus.Text = "Brak tekstu do wklejenia.";
                                    return;
                                }

                                if (!CopyToClipboard(_fullTranscript))
                                {
                                    TxtStatus.Text = "Nie udało się skopiować tekstu do schowka.";
                                    return;
                                }

                                if (!SendCtrlV())
                                {
                                    TxtStatus.Text = "Nie udało się wysłać skrótu Ctrl+V.";
                                    return;
                                }

                                ClearTranscript(clearClipboard: false);
                                TxtStatus.Text = _isListening ? "Słucham... (wklejono)" : "Zatrzymano";
                            });
                        }
                        else
                        {
                            // Dedup: jesli nowy kawalek zaczyna sie od tego samego co
                            // poprzedni (model powtorzyl zdanie przez sztuczne ciecie VAD),
                            // nie dopisuj go drugi raz.
                            var lastLine = _fullTranscript.Length == 0 ? "" :
                                _fullTranscript.Split('\n')[^1].Trim();
                            if (!string.IsNullOrEmpty(lastLine) &&
                                (text.StartsWith(lastLine) || lastLine.StartsWith(text)))
                            {
                                TxtStatus.Text = _isListening ? "Słucham..." : "Zatrzymano";
                            }
                            else
                            {
                                DispatcherQueue.TryEnqueue(() =>
                                {
                                    _fullTranscript = string.IsNullOrEmpty(_fullTranscript)
                                        ? text : _fullTranscript + "\n" + text;
                                    TxtResult.Text = _fullTranscript;
                                    ScrollResultsToBottom();
                                    TxtStatus.Text = _isListening ? "Słucham..." : "Zatrzymano";

                                    if (_settings.AutoCopy)
                                        CopyToClipboard(_fullTranscript);
                                });
                            }
                        }
                    }
                }
                else
                {
                    var msg = result.Ok ? "Brak mowy w fragmencie" : result.Error;
                    DispatcherQueue.TryEnqueue(() =>
                        TxtStatus.Text = _isListening ? $"Słucham... ({msg})" : "Zatrzymano");
                }
            }
            catch (OperationCanceledException) when (_closing)
            {
            }
            catch (Exception ex)
            {
                if (_closing) return;
                // Serwer mogl nie odpowiedziec (timeout/zamkniety) - nie wieszaj apki.
                DispatcherQueue.TryEnqueue(() =>
                {
                    TxtStatus.Text = $"Błąd serwera: {ex.Message}";
                    BtnRecord.IsEnabled = true;   // pozwol sprobowac ponownie
                });
            }
            finally
            {
                _flushing = false;
            }
        }

        private bool CopyToClipboard(string text)
        {
            try
            {
                var dataPackage = new DataPackage();
                dataPackage.SetText(text);
                Clipboard.SetContent(dataPackage);
                Clipboard.Flush();
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Schowek: {ex.Message}");
                return false;
            }
        }

        private static bool SendCtrlV()
        {
            const uint inputKeyboard = 1;
            const uint keyUp = 0x0002;
            const ushort virtualKeyControl = 0x11;
            const ushort virtualKeyV = 0x56;

            var inputs = new[]
            {
                new INPUT { Type = inputKeyboard, Data = new InputUnion { Keyboard = new KEYBDINPUT { VirtualKey = virtualKeyControl } } },
                new INPUT { Type = inputKeyboard, Data = new InputUnion { Keyboard = new KEYBDINPUT { VirtualKey = virtualKeyV } } },
                new INPUT { Type = inputKeyboard, Data = new InputUnion { Keyboard = new KEYBDINPUT { VirtualKey = virtualKeyV, Flags = keyUp } } },
                new INPUT { Type = inputKeyboard, Data = new InputUnion { Keyboard = new KEYBDINPUT { VirtualKey = virtualKeyControl, Flags = keyUp } } }
            };

            return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint numberOfInputs, INPUT[] inputs, int sizeOfInput);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint Type;
            public InputUnion Data;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)]
            public KEYBDINPUT Keyboard;

            [FieldOffset(0)]
            public MOUSEINPUT Mouse;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort VirtualKey;
            public ushort ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int Dx;
            public int Dy;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        // Czysci transkrypcje na ekranie i opcjonalnie schowek systemowy.
        private void ClearTranscript(bool clearClipboard = true)
        {
            _fullTranscript = "";
            TxtResult.Text = "";
            BtnCopy.Visibility = Visibility.Collapsed;
            if (!clearClipboard) return;

            try
            {
                // Wyczysc schowek (pusty pakiet danych).
                Clipboard.SetContent(new DataPackage());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Schowek-czyszczenie: {ex.Message}");
            }
        }

        // Przewin listy wynikow na sam dol przy kazdym nowym tekscie.
        private void ScrollResultsToBottom()
        {
            if (ResultScroll == null) return;
            ResultScroll.ChangeView(null, ResultScroll.ScrollableHeight, null);
        }

        #region Ustawienia (suwak pauzy + auto-kopiowanie, zapis do JSON)

        private void SliderSilence_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            _silenceFlushMs = (int)e.NewValue;
            if (TxtSilenceLabel != null)
                TxtSilenceLabel.Text = $"Czas pauzy: {_silenceFlushMs} ms";
            if (!_settingsLoaded) return;   // nie zapisuj podczas inicjalizacji
            _settings.SilenceFlushMs = _silenceFlushMs;
            _settings.Save();
        }

        private void ChkAutoCopy_Click(object sender, RoutedEventArgs e)
        {
            if (!_settingsLoaded) return;
            _settings.AutoCopy = ChkAutoCopy.IsChecked == true;
            _settings.Save();
        }

        #endregion

        private void CleanupRecording()
        {
            if (_waveIn != null)
            {
                _waveIn.DataAvailable -= OnAudioDataAvailable;
                _waveIn.Dispose();
                _waveIn = null;
            }
        }
    }
}
