using Microsoft.Win32;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PdfTextEditor;

/// <summary>
/// Windows 기본 앱 설정에 이 프로그램을 PDF 열기 후보로 등록한다.
/// (HKCU — 관리자 권한 불필요. 사용자가 설정 > 기본 앱에서 PDF 기본 프로그램으로 선택)
/// </summary>
public static class FileAssociation
{
    const string ProgId = "PdfTextEditor.PDF";
    const string AppName = "PDF 편집기";
    const string CapabilitiesKey = @"Software\PdfTextEditor\Capabilities";
    const string RegisteredAppsKey = @"Software\RegisteredApplications";

    public static string ExePath
    {
        get
        {
            // single-file / framework-dependent 모두 대응
            var p = Environment.ProcessPath
                 ?? Process.GetCurrentProcess().MainModule?.FileName
                 ?? Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(p))
                p = Path.Combine(AppContext.BaseDirectory, "PdfTextEditor.exe");
            return Path.GetFullPath(p);
        }
    }

    public static bool IsRegistered()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(CapabilitiesKey);
            return k != null;
        }
        catch { return false; }
    }

    /// <summary>현재 사용자 레지스트리에 등록. 성공 시 true.</summary>
    public static bool Register()
    {
        string exe = ExePath;
        if (!File.Exists(exe))
            throw new FileNotFoundException("실행 파일을 찾을 수 없습니다. 설치/게시된 exe로 실행해 주세요.", exe);

        string command = $"\"{exe}\" \"%1\"";
        string icon = $"\"{exe}\",0";

        // 1) ProgID
        using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            prog.SetValue("", "PDF 문서");
            prog.SetValue("FriendlyTypeName", "PDF 문서 (PDF 편집기)");
            using (var defIcon = prog.CreateSubKey("DefaultIcon"))
                defIcon.SetValue("", icon);
            using (var open = prog.CreateSubKey(@"shell\open"))
            {
                open.SetValue("", "PDF 편집기로 열기");
                using var cmd = open.CreateSubKey("command");
                cmd.SetValue("", command);
            }
        }

        // 2) .pdf OpenWithProgids (기본 연결을 덮어쓰지 않고 후보만 추가)
        using (var pdf = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.pdf\OpenWithProgids"))
        {
            pdf.SetValue(ProgId, new byte[0], RegistryValueKind.None);
        }
        // Applications 목록 (열기 프로그램에 표시)
        using (var apps = Registry.CurrentUser.CreateSubKey($@"Software\Classes\Applications\{Path.GetFileName(exe)}\shell\open\command"))
        {
            apps.SetValue("", command);
        }

        // 3) Default Programs / 설정 > 기본 앱 에 나타나게 Capabilities
        using (var cap = Registry.CurrentUser.CreateSubKey(CapabilitiesKey))
        {
            cap.SetValue("ApplicationName", AppName);
            cap.SetValue("ApplicationDescription", "텍스트·이미지·서명을 편집할 수 있는 PDF 편집기");
            using (var fa = cap.CreateSubKey("FileAssociations"))
                fa.SetValue(".pdf", ProgId);
        }
        using (var reg = Registry.CurrentUser.CreateSubKey(RegisteredAppsKey))
        {
            reg.SetValue("PdfTextEditor", CapabilitiesKey);
        }

        // 셸 아이콘/연결 캐시 갱신 알림
        try { SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero); } catch { }

        return true;
    }

    public static void Unregister()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", false); } catch { }
        try
        {
            using var pdf = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.pdf\OpenWithProgids", true);
            pdf?.DeleteValue(ProgId, false);
        }
        catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\PdfTextEditor", false); } catch { }
        try
        {
            using var reg = Registry.CurrentUser.OpenSubKey(RegisteredAppsKey, true);
            reg?.DeleteValue("PdfTextEditor", false);
        }
        catch { }
        try { SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero); } catch { }
    }

    /// <summary>Windows 기본 앱 설정 화면을 연다 (PDF 파일 형식).</summary>
    public static void OpenWindowsDefaultApps()
    {
        try
        {
            // Windows 10/11 설정 — 기본 앱 by file type
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:defaultapps",
                UseShellExecute = true,
            });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "control",
                    Arguments = "/name Microsoft.DefaultPrograms",
                    UseShellExecute = true,
                });
            }
            catch { /* ignore */ }
        }
    }

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
