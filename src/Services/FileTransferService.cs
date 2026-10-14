using System.IO;
using System.Net;
using System.Net.Sockets;
using AirCode.Models;
using Newtonsoft.Json;

namespace AirCode.Services;

/// <summary>
/// Direct TCP file transfer between peers.
/// Sender opens a TCP listener on a random port; receiver connects to it.
/// The transfer is negotiated via WebSocket signalling.
/// </summary>
public class FileTransferService : IDisposable
{
    private const int ChunkSize = 256 * 1024; // 256 KB chunks
    private const long MaxFileSize = 2L * 1024 * 1024 * 1024; // 2 GB limit

    private bool _disposed;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> _sends = new();

    public event Action<FileTransfer>? TransferStarted;
    public event Action<FileTransfer>? TransferCompleted;
    public event Action<FileTransfer>? TransferFailed;
    public event Action<FileTransfer>? TransferProgress;

    // ── Send ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a TCP server to send a file; returns the port the receiver should connect to.
    /// Runs the send loop in background and updates the FileTransfer progress.
    /// </summary>
    public async Task<int> BeginSendAsync(FileTransfer transfer, string filePath,
        CancellationToken ct = default)
    {
        var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // Wait up to 2 minutes for the receiver to accept (the offer dialog needs a human)
        var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts2.CancelAfter(TimeSpan.FromMinutes(2));
        _sends[transfer.Id] = cts2;

        _ = Task.Run(async () =>
        {
            try
            {
                using var client = await AcceptWithTimeoutAsync(listener, cts2.Token);
                cts2.CancelAfter(TimeSpan.FromHours(2)); // reset after connect

                transfer.Status = TransferStatus.Active;
                TransferStarted?.Invoke(transfer);

                using var stream = client.GetStream();
                using var file = File.OpenRead(filePath);

                var buffer = new byte[ChunkSize];
                long sent = 0;
                long lastBytes = 0;
                var speedTimer = DateTime.UtcNow;

                int read;
                while ((read = await file.ReadAsync(buffer, 0, buffer.Length, cts2.Token)) > 0)
                {
                    await stream.WriteAsync(buffer, 0, read, cts2.Token);
                    sent += read;
                    transfer.BytesTransferred = sent;
                    transfer.Progress = transfer.FileSize == 0 ? 100 : (double)sent / transfer.FileSize * 100;

                    // update speed every 500ms
                    if ((DateTime.UtcNow - speedTimer).TotalMilliseconds >= 500)
                    {
                        var elapsed = (DateTime.UtcNow - speedTimer).TotalSeconds;
                        var bytesPerSec = (sent - lastBytes) / elapsed;
                        transfer.SpeedText = FileTransfer.FormatSize((long)bytesPerSec) + "/s";
                        lastBytes = sent;
                        speedTimer = DateTime.UtcNow;
                        TransferProgress?.Invoke(transfer);
                    }
                }

                await stream.FlushAsync(cts2.Token);
                transfer.Status = TransferStatus.Completed;
                transfer.Progress = 100;
                transfer.CompletedAt = DateTime.Now;
                transfer.SpeedText = "";
                TransferCompleted?.Invoke(transfer);
            }
            catch (OperationCanceledException)
            {
                // A decline already marked the transfer — don't report that as a failure
                if (transfer.Status != TransferStatus.Declined)
                {
                    transfer.Status = TransferStatus.Cancelled;
                    TransferFailed?.Invoke(transfer);
                }
            }
            catch
            {
                transfer.Status = TransferStatus.Failed;
                TransferFailed?.Invoke(transfer);
            }
            finally
            {
                listener.Stop();
                _sends.TryRemove(transfer.Id, out _);
                cts2.Dispose();
            }
        });

        return port;
    }

    /// <summary>Stops waiting for a receiver (they declined, or the sender cancelled).</summary>
    public void CancelSend(string transferId)
    {
        if (_sends.TryGetValue(transferId, out var cts))
            try { cts.Cancel(); } catch { }
    }

    // ── Receive ───────────────────────────────────────────────────────────────

    public async Task ReceiveFileAsync(FileTransfer transfer, string senderIp, int port,
        string savePath, CancellationToken ct = default)
    {
        try
        {
            using var client = new TcpClient();
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(senderIp, port, connectCts.Token);

            transfer.Status = TransferStatus.Active;
            transfer.SavePath = savePath;
            TransferStarted?.Invoke(transfer);

            Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);

            using var stream = client.GetStream();
            using var file = File.Create(savePath);

            var buffer = new byte[ChunkSize];
            long received = 0;
            var speedTimer = DateTime.UtcNow;
            long lastBytes = 0;

            int read;
            while (received < transfer.FileSize &&
                   (read = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await file.WriteAsync(buffer, 0, read, ct);
                received += read;
                transfer.BytesTransferred = received;
                transfer.Progress = transfer.FileSize == 0 ? 100 : (double)received / transfer.FileSize * 100;

                if ((DateTime.UtcNow - speedTimer).TotalMilliseconds >= 500)
                {
                    var elapsed = (DateTime.UtcNow - speedTimer).TotalSeconds;
                    var bps = (received - lastBytes) / elapsed;
                    transfer.SpeedText = FileTransfer.FormatSize((long)bps) + "/s";
                    lastBytes = received;
                    speedTimer = DateTime.UtcNow;
                    TransferProgress?.Invoke(transfer);
                }
            }

            if (received < transfer.FileSize)
                throw new IOException("Connection closed before the file finished.");

            transfer.Status = TransferStatus.Completed;
            transfer.Progress = 100;
            transfer.CompletedAt = DateTime.Now;
            transfer.SpeedText = "";
            TransferCompleted?.Invoke(transfer);
        }
        catch (OperationCanceledException)
        {
            transfer.Status = TransferStatus.Cancelled;
            if (File.Exists(savePath)) File.Delete(savePath);
            TransferFailed?.Invoke(transfer);
        }
        catch
        {
            transfer.Status = TransferStatus.Failed;
            if (File.Exists(savePath)) File.Delete(savePath);
            TransferFailed?.Invoke(transfer);
        }
    }

    private static async Task<TcpClient> AcceptWithTimeoutAsync(TcpListener listener, CancellationToken ct)
    {
        using (ct.Register(() => { try { listener.Stop(); } catch { } }))
        {
            try { return await listener.AcceptTcpClientAsync(); }
            catch (Exception) when (ct.IsCancellationRequested)
            { throw new OperationCanceledException(ct); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
