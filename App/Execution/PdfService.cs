using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ByodKioskBrowser.Execution;

/// <summary>
/// 由 Google Drive 下載公開題目 PDF（由 C# 抓取，非瀏覽器導航，因此不需將 google 加入白名單、
/// 學生也無法藉此存取自己的雲端硬碟）。
/// 支援來源：資料夾連結/ID（自動找裡面的 PDF）、檔案連結/ID。
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

    /// <summary>依來源（資料夾/檔案 連結或 ID）下載題目 PDF 到暫存檔，回傳路徑。</summary>
    public static async Task<string> DownloadExamPdfAsync(string source)
    {
        source = source.Trim();

        var folderId = Match(source, @"/folders/([-\w]{15,})");
        if (folderId != null)
        {
            var fileId = await FindPdfInFolderAsync(folderId)
                ?? throw new InvalidDataException("該 Drive 資料夾中找不到 PDF（請確認資料夾為公開、且內含 PDF）。");
            return await DownloadFileAsync(fileId);
        }

        // 檔案連結或裸 ID
        var id = Match(source, @"/file/d/([-\w]{15,})")
                 ?? Match(source, @"[?&]id=([-\w]{15,})")
                 ?? (Regex.IsMatch(source, @"^[-\w]{15,}$") ? source : null)
                 ?? throw new InvalidDataException("無法解析 Drive 來源（請貼上資料夾或檔案的分享連結）。");
        return await DownloadFileAsync(id);
    }

    /// <summary>讀取公開資料夾清單，回傳第一個 PDF 的檔案 ID（無需 API 金鑰）。</summary>
    private static async Task<string?> FindPdfInFolderAsync(string folderId)
    {
        var url = $"https://drive.google.com/embeddedfolderview?id={folderId}#list";
        var html = await Http.GetStringAsync(url);

        // 逐一取出 (檔名, 檔案ID)；embeddedfolderview 的項目含 id="entry-<ID>" 與標題
        var ids = Regex.Matches(html, @"entry-([-\w]{15,})").Select(m => m.Groups[1].Value).ToList();
        var titles = Regex.Matches(html, @"flip-entry-title[^>]*>([^<]+)<").Select(m => m.Groups[1].Value).ToList();

        // 優先挑檔名以 .pdf 結尾者；配對數不足時退回第一個項目
        for (var i = 0; i < ids.Count && i < titles.Count; i++)
        {
            if (titles[i].Trim().EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                return ids[i];
            }
        }
        return ids.FirstOrDefault();
    }

    /// <summary>以檔案 ID 下載公開 PDF；驗證確為 PDF。</summary>
    private static async Task<string> DownloadFileAsync(string fileId)
    {
        var url = $"https://drive.google.com/uc?export=download&id={fileId}";
        var bytes = await Http.GetByteArrayAsync(url);

        if (bytes.Length < 5 || bytes[0] != (byte)'%' || bytes[1] != (byte)'P' || bytes[2] != (byte)'D' || bytes[3] != (byte)'F')
        {
            throw new InvalidDataException("下載的內容不是 PDF（請確認檔案為公開、且非過大觸發掃描頁）。");
        }

        var path = Path.Combine(Path.GetTempPath(), "byod_exam_" + Guid.NewGuid().ToString("N") + ".pdf");
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    private static string? Match(string input, string pattern)
    {
        var m = Regex.Match(input, pattern);
        return m.Success ? m.Groups[1].Value : null;
    }
}
