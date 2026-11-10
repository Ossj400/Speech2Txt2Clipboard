using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SpeechToTextAI;

/// <summary>
/// Klient lokalnego serwera MOSS Transcribe-Diarize (Python/FastAPI).
///
/// MOSS dziala WSADOWO: bierze caly plik audio/wideo i zwraca transkrypcje
/// z podzialem na mowcow ([S01], [S02]...) oraz znacznikami czasu.
/// To inny tryb niz strumieniowy Sherpa-ONNX (na zywo z mikrofonu).
///
/// Wymaga uruchomionego serwera:
///     Z:\models\MOSS-Transcribe-Diarize\serve_moss.bat
/// Domyslnie: http://127.0.0.1:8760
/// </summary>
public sealed class MossTranscriptionService : IDisposable
{
    private readonly HttpClient _http;
    private readonly object _processLock = new();
    private OwnedServerProcess? _serverProcess;
    private bool _disposed;

    public string BaseUrl { get; }

    public MossTranscriptionService(string baseUrl = "http://127.0.0.1:8760")
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient
        {
            // dlugie nagrania (do 90 min) moga sie liczyc kilka minut
            Timeout = TimeSpan.FromMinutes(30)
        };
    }

    /// <summary>
    /// Pojedynczy segment wypowiedzi: kto, kiedy, co.
    /// </summary>
    public sealed class TranscriptSegment
    {
        [JsonPropertyName("start")] public double Start { get; set; }
        [JsonPropertyName("end")] public double End { get; set; }
        [JsonPropertyName("speaker")] public string Speaker { get; set; } = "";
        [JsonPropertyName("text")] public string Text { get; set; } = "";
    }

    /// <summary>
    /// Pelny wynik transkrypcji.
    /// </summary>
    public sealed class TranscriptResult
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("text")] public string Text { get; set; } = "";
        [JsonPropertyName("segments")] public List<TranscriptSegment> Segments { get; set; } = new();
        [JsonPropertyName("elapsed_sec")] public double ElapsedSec { get; set; }
        [JsonPropertyName("device")] public string Device { get; set; } = "";
        [JsonPropertyName("error")] public string? Error { get; set; }
    }

    private sealed class HealthResponse
    {
        [JsonPropertyName("status")] public string Status { get; set; } = "";
        [JsonPropertyName("model_loaded")] public bool ModelLoaded { get; set; }
        [JsonPropertyName("device")] public string Device { get; set; } = "";
    }

    /// <summary>
    /// Sprawdza czy serwer dziala i model jest zaladowany.
    /// </summary>
    public async Task<bool> IsServerReadyAsync(CancellationToken ct = default)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
            using var resp = await _http.GetAsync($"{BaseUrl}/health", timeoutCts.Token);
            if (!resp.IsSuccessStatusCode) return false;
            var health = await resp.Content.ReadFromJsonAsync<HealthResponse>(cancellationToken: timeoutCts.Token);
            return health is { ModelLoaded: true };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Sciezka do pliku uruchamiajacego serwer MOSS.
    /// </summary>
    public string ServerBatPath { get; set; } =
        @"Z:\models\MOSS-Transcribe-Diarize\serve_moss.bat";

    /// <summary>
    /// Zapewnia ze serwer dziala. Jesli nie odpowiada - uruchamia serve_moss.bat
    /// w tle i czeka az model sie zaladuje (odpytuje /health).
    /// </summary>
    /// <param name="progress">opcjonalny callback statusu (do wyswietlenia w UI)</param>
    /// <param name="maxWaitSeconds">maks. czas oczekiwania na zaladowanie modelu</param>
    /// <returns>true gdy serwer gotowy, false gdy sie nie udalo</returns>
    public async Task<bool> EnsureServerRunningAsync(
        Action<string>? progress = null,
        int maxWaitSeconds = 120,
        CancellationToken ct = default)
    {
        // 1) Moze juz dziala?
        if (await IsServerReadyAsync(ct))
            return true;

        // Nie uruchamiaj kolejnego modelu, jesli proces juz zajal port,
        // nawet gdy model nadal sie laduje albo jego endpoint chwilowo nie odpowiada.
        if (!await IsServerListeningAsync(ct))
        {
            // 2) Sprobuj uruchomic .bat
            if (!File.Exists(ServerBatPath))
            {
                progress?.Invoke($"Nie znaleziono: {ServerBatPath}");
                return false;
            }

            progress?.Invoke("Uruchamiam serwer MOSS...");
            try
            {
                lock (_processLock)
                {
                    ct.ThrowIfCancellationRequested();
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_serverProcess == null || _serverProcess.HasExited)
                    {
                        _serverProcess?.Dispose();
                        _serverProcess = OwnedServerProcess.Start(ServerBatPath);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
            catch (Exception ex)
            {
                progress?.Invoke($"Nie udalo sie uruchomic serwera: {ex.Message}");
                return false;
            }
        }
        else
        {
            progress?.Invoke("Serwer MOSS juz nasluchuje; czekam na gotowosc modelu...");
        }

        // 3) Czekaj az /health zwroci model_loaded=true
        for (int elapsed = 0; elapsed < maxWaitSeconds; elapsed++)
        {
            if (ct.IsCancellationRequested) return false;
            await Task.Delay(1000, ct);
            if (await IsServerReadyAsync(ct))
            {
                progress?.Invoke("Serwer gotowy.");
                return true;
            }
            progress?.Invoke($"Ladowanie modelu... ({elapsed + 1}s)");
        }

        progress?.Invoke("Przekroczono czas oczekiwania na serwer.");
        return false;
    }

    private async Task<bool> IsServerListeningAsync(CancellationToken ct)
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri))
            return false;

        using var client = new TcpClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            await client.ConnectAsync(uri.Host, uri.Port, timeoutCts.Token);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// Wysyla plik audio/wideo do serwera MOSS i zwraca transkrypcje z mowcami.
    /// </summary>
    public async Task<TranscriptResult> TranscribeFileAsync(string filePath, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!File.Exists(filePath))
            throw new FileNotFoundException("Nie znaleziono pliku audio/wideo.", filePath);

        using var form = new MultipartFormDataContent();
        await using var fs = File.OpenRead(filePath);
        var fileContent = new StreamContent(fs);
        form.Add(fileContent, "file", Path.GetFileName(filePath));

        using var resp = await _http.PostAsync($"{BaseUrl}/transcribe", form, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);

        var result = JsonSerializer.Deserialize<TranscriptResult>(json)
                     ?? new TranscriptResult { Ok = false, Error = "Pusta odpowiedz serwera." };

        if (!resp.IsSuccessStatusCode && string.IsNullOrEmpty(result.Error))
            result.Error = $"Serwer zwrocil {(int)resp.StatusCode}.";

        return result;
    }

    /// <summary>
    /// Wysyla surowe probki audio (float[-1..1], mono) prosto z pamieci do MOSS.
    /// Uzywane przez tryb nasluchu mikrofonu (bez plikow tymczasowych).
    /// lang="pl" wymusza polski (model inaczej sam zgaduje jezyk).
    /// </summary>
    public async Task<TranscriptResult> TranscribeSamplesAsync(
        float[] samples, int sampleRate = 16000, string lang = "pl", CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var wav = BuildWav16(samples, sampleRate);

        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(wav);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        form.Add(content, "file", "chunk.wav");

        var url = $"{BaseUrl}/transcribe?lang={Uri.EscapeDataString(lang)}";
        // Twardy timeout na wypadek gdyby model sie zawiesil (np. na ciszy).
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(120));
        using var resp = await _http.PostAsync(url, form, cts.Token);
        var json = await resp.Content.ReadAsStringAsync(cts.Token);

        var result = JsonSerializer.Deserialize<TranscriptResult>(json)
                     ?? new TranscriptResult { Ok = false, Error = "Pusta odpowiedz serwera." };

        if (!resp.IsSuccessStatusCode && string.IsNullOrEmpty(result.Error))
            result.Error = $"Serwer zwrocil {(int)resp.StatusCode}.";

        return result;
    }

    /// <summary>
    /// Buduje minimalny plik WAV (PCM 16-bit mono) z probek float w pamieci.
    /// </summary>
    private static byte[] BuildWav16(float[] samples, int sampleRate)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        int dataBytes = samples.Length * 2;

        bw.Write(new[] { 'R', 'I', 'F', 'F' });
        bw.Write(36 + dataBytes);
        bw.Write(new[] { 'W', 'A', 'V', 'E' });
        bw.Write(new[] { 'f', 'm', 't', ' ' });
        bw.Write(16);                 // rozmiar fmt
        bw.Write((short)1);           // PCM
        bw.Write((short)1);           // mono
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);     // byte rate
        bw.Write((short)2);           // block align
        bw.Write((short)16);          // bits per sample
        bw.Write(new[] { 'd', 'a', 't', 'a' });
        bw.Write(dataBytes);
        foreach (var s in samples)
        {
            short v = (short)Math.Clamp(s * 32767f, short.MinValue, short.MaxValue);
            bw.Write(v);
        }
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// Formatuje wynik do czystego tekstu (bez etykiet mowcow ani znacznikow czasu).
    /// Kazdy segment w nowej linii. Przyklad:  Witam wszystkich...
    /// </summary>
    public static string FormatReadable(TranscriptResult result)
    {
        if (!result.Ok)
            return $"Blad: {result.Error}";

        var sb = new System.Text.StringBuilder();
        foreach (var seg in result.Segments)
            sb.AppendLine(seg.Text.Trim());
        return sb.ToString().TrimEnd();
    }

    public void Dispose()
    {
        OwnedServerProcess? serverProcess;
        lock (_processLock)
        {
            if (_disposed) return;
            _disposed = true;
            serverProcess = _serverProcess;
            _serverProcess = null;
        }

        _http.Dispose();
        if (serverProcess == null) return;

        serverProcess.Dispose();
    }
}
