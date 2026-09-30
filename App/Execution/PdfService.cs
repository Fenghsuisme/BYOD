using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace ByodKioskBrowser.Execution;

/// <summary>
/// 由 Google Drive 下載公開 PDF（由 C# 抓取，非瀏覽器導航，因此不需將 google 加入白名單、
/// 學生也無法藉此存取自己的雲端硬碟）。
/// </summary>
public static class PdfService
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    static PdfService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (BYOD-Kiosk)");
    }

    /// <summary>下載指定 Drive 檔案 ID 的公開 PDF 到暫存檔，回傳檔案路徑。</summary>
    public static async Task<string> DownloadDrivePdfAsync(string fileId)
    {
        var url = $"https://drive.google.com/uc?export=download&id={Uri.EscapeDataString(fileId)}";
        var bytes = await Http.GetByteArrayAsync(url);

        // 基本檢查：PDF 以 "%PDF" 開頭；若非（例如大檔的病毒掃描頁）則視為失敗
        if (bytes.Length < 5 || bytes[0] != (byte)'%' || bytes[1] != (byte)'P' || bytes[2] != (byte)'D' || bytes[3] != (byte)'F')
        {
            throw new InvalidDataException("下載的內容不是 PDF（請確認檔案為公開、且非過大觸發掃描頁）。");
        }

        var path = Path.Combine(Path.GetTempPath(), "byod_exam_" + Guid.NewGuid().ToString("N") + ".pdf");
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }
}
