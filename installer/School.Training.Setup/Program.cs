using System.Diagnostics;
using System.ComponentModel;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

Console.OutputEncoding = Encoding.UTF8;
if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem)
{
    Console.Error.WriteLine("本安装包仅适用于受支持的 64 位 Windows 系统。");
    return 1;
}
var quiet = args.Contains("--quiet", StringComparer.OrdinalIgnoreCase);
var validateOnly = args.Contains("--validate-only", StringComparer.OrdinalIgnoreCase);
string GetOption(string name, string fallback)
{
    var index = Array.FindIndex(args, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
    if (index < 0) return fallback;
    if (index + 1 >= args.Length || args[index + 1].StartsWith("--")) throw new ArgumentException($"缺少参数值：{name}");
    return args[index + 1];
}

StagingArea? staging = null;
try
{
    var installDir = Path.GetFullPath(GetOption("--install-dir", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SchoolTraining")));
    var dataDir = Path.GetFullPath(GetOption("--data-dir", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SchoolTraining")));
    var listen = GetOption("--listen-address", "127.0.0.1");
    var portText = GetOption("--port", "5080");
    var allowLan = args.Contains("--allow-lan", StringComparer.OrdinalIgnoreCase);
    var scheme = allowLan ? "https" : "http";
    var serverConfig = GetOption("--server-config", "");
    if (!int.TryParse(portText, out var port) || port is < 1024 or > 65535) throw new ArgumentException("访问端口必须在 1024–65535 之间。");
    var confirmed = args.Contains("--confirmed", StringComparer.OrdinalIgnoreCase);
    if (!quiet && !validateOnly && !confirmed)
    {
        var message = $"将安装学校实训资源平台。\n\n程序目录：{installDir}\n数据目录：{dataDir}\n访问地址：{scheme}://{listen}:{port}\n\n安装会注册 Windows 后台服务。升级时保留数据库和密钥。\n是否继续？";
        if (Native.MessageBox(IntPtr.Zero, message, "学校实训资源平台安装", 0x24) != 6) return 0;
    }
    using var identity = WindowsIdentity.GetCurrent();
    if (!validateOnly && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位安装程序。");
        if (Path.GetFileName(executable).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请运行发布后的 Setup.exe 进行安装。");
        var elevate = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas" };
        foreach (var value in args) elevate.ArgumentList.Add(value);
        elevate.ArgumentList.Add("--confirmed");
        using var elevated = Process.Start(elevate) ?? throw new InvalidOperationException("未启动管理员安装过程。");
        await elevated.WaitForExitAsync();
        return elevated.ExitCode;
    }
    staging = StagingArea.Create(validateOnly);
    using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("SchoolTraining.Payload.zip") ?? throw new InvalidOperationException("安装载荷缺失，请使用 build-release.ps1 生成安装包。");
    using (var zip = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true)) staging.Extract(zip);
    var app = staging.Resolve("app/School.Training.exe");
    var installScript = staging.Resolve("scripts/install-service.ps1");
    if (!File.Exists(app) || !File.Exists(installScript) || !File.Exists(staging.Resolve("release.json"))) throw new InvalidDataException("安装载荷不完整。");
    if (validateOnly)
    {
        Console.WriteLine("Payload validation passed. No service, firewall or installation changes were performed.");
        return 0;
    }
    var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    staging.VerifyTree();
    foreach (var value in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", installScript, "-SourceDirectory", staging.Resolve("app"), "-InstallDirectory", installDir, "-DataDirectory", dataDir }) start.ArgumentList.Add(value);
    if (args.Contains("--listen-address", StringComparer.OrdinalIgnoreCase)) { start.ArgumentList.Add("-ListenAddress"); start.ArgumentList.Add(listen); }
    if (args.Contains("--port", StringComparer.OrdinalIgnoreCase)) { start.ArgumentList.Add("-Port"); start.ArgumentList.Add(portText); }
    if (allowLan) start.ArgumentList.Add("-AllowLan");
    if (serverConfig.Length > 0) { start.ArgumentList.Add("-ServerConfigurationFile"); start.ArgumentList.Add(Path.GetFullPath(serverConfig)); }
    using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动安装过程。");
    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var output = await outputTask;
    var error = await errorTask;
    Console.WriteLine(output);
    if (process.ExitCode != 0) throw new InvalidOperationException(error.Length > 0 ? error : $"安装失败，退出码 {process.ExitCode}");
    if (!quiet) Native.MessageBox(IntPtr.Zero, $"安装完成。\n\n请在服务器浏览器打开已配置的访问地址初始化管理员。\n默认本机地址：http://127.0.0.1:5080\nHTTPS 请使用证书匹配的主机名。\n数据目录：{dataDir}", "学校实训资源平台", 0x40);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    if (!quiet) Native.MessageBox(IntPtr.Zero, ex.Message, "安装未完成", 0x10);
    return 1;
}
finally
{
    try { staging?.Dispose(); }
    catch (Exception ex) { Console.Error.WriteLine("临时目录未清理；目录身份或权限验证失败，已停止删除：" + ex.Message); }
}

// One installer-owned directory per attempt. Installation staging is deliberately
// outside user TEMP; every created object has an explicit Administrators owner and
// a protected DACL, including files (not merely the top-level directory).
internal sealed class StagingArea : IDisposable
{
    private const string Prefix = "SchoolTraining-Setup-";
    private const string ProtectedSddl = "O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";
    private const int MaxEntries = 25000;
    private const long MaxEntryBytes = 512L * 1024 * 1024;
    private const long MaxExpandedBytes = 2L * 1024 * 1024 * 1024;
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemIdentity = new(WellKnownSidType.LocalSystemSid, null);
    private readonly string parent;
    private readonly bool protectedMode;
    private readonly List<SafeFileHandle> ancestorLocks = [];
    private readonly Dictionary<string, SafeFileHandle> directoryLocks = new(StringComparer.OrdinalIgnoreCase);
    private IntPtr descriptor;
    private bool owned;
    private bool disposed;
    private bool extracted;
    public string Root { get; }

    private StagingArea(string parent, bool protectedMode)
    {
        this.parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        this.protectedMode = protectedMode;
        Root = Path.Combine(this.parent, Prefix + Guid.NewGuid().ToString("N"));
    }

    public static StagingArea Create(bool validateOnly)
    {
        if (!validateOnly)
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new SecurityException("正式安装需要已提升的管理员进程。");
        }
        var parent = validateOnly ? Path.GetTempPath() : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var area = new StagingArea(parent, !validateOnly);
        try
        {
            area.LockAncestors();
            if (!validateOnly) ValidateProtectedParent(parent);
            if (!validateOnly && !Native.ConvertStringSecurityDescriptorToSecurityDescriptor(ProtectedSddl, 1, out area.descriptor, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建安装目录安全描述符。");
            var attributes = area.SecurityAttributes();
            // CREATE_DIRECTORY with SECURITY_ATTRIBUTES applies ownership and DACL
            // at creation. Existing directories are never adopted, even after a GUID collision.
            if (!Native.CreateDirectory(area.Root, ref attributes)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建独占安装临时目录。");
            area.owned = true;
            area.directoryLocks.Add(area.Root, OpenDirectoryLock(area.Root));
            area.VerifyObject(area.Root, true);
            return area;
        }
        catch
        {
            try { area.Dispose(); } catch { /* Fail closed: do not delete an unverified directory. */ }
            throw;
        }
    }

    public static void ValidateProtectedParent(string directory)
    {
        EnsureNoReparse(directory);
        var acl = new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        ValidateProtectedParentAcl(acl);
    }

    internal static void ValidateProtectedParentAcl(DirectorySecurity acl)
    {
        if (new RawSecurityDescriptor(acl.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl == null)
            throw new SecurityException("安装临时目录的父目录不能使用空安全控制表。");
        var trusted = new HashSet<string>(StringComparer.Ordinal) { Administrators.Value, SystemIdentity.Value };
        try { trusted.Add(((SecurityIdentifier)new NTAccount("NT SERVICE", "TrustedInstaller").Translate(typeof(SecurityIdentifier))).Value); }
        catch (IdentityNotMappedException) { }
        if (acl.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !trusted.Contains(owner.Value))
            throw new SecurityException("安装临时目录的父目录不是受信任的系统目录。");
        const FileSystemRights dangerous = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | (FileSystemRights)0x50000000; // GENERIC_WRITE | GENERIC_ALL
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
            if (!trusted.Contains(rule.IdentityReference.Value) && (rule.FileSystemRights & dangerous) != 0)
                throw new SecurityException("安装临时目录的父目录允许非管理员写入，已拒绝使用。");
        }
    }

    public string Resolve(string relative)
    {
        var normalized = relative.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/') || normalized.Contains(':') || Path.IsPathRooted(relative))
            throw new InvalidDataException("载荷包含绝对路径或数据流路径。");
        var parts = normalized.TrimEnd('/').Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsReservedName(part)))
            throw new InvalidDataException("载荷包含不安全的文件名。");
        var result = Path.GetFullPath(Path.Combine(Root, Path.Combine(parts)));
        if (!result.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("载荷路径越出安装临时目录。");
        return result;
    }

    private static bool IsReservedName(string part)
    {
        var stem = part.Split('.')[0].TrimEnd(' ', '.').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9';
    }

    public void Extract(ZipArchive archive)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (extracted) throw new InvalidOperationException("一个安装目录只允许解压一份载荷。");
        extracted = true;
        if (archive.Entries.Count > MaxEntries) throw new InvalidDataException("安装载荷条目过多。");
        long expanded = 0;
        var plans = new List<(ZipArchiveEntry Entry, string Path, bool Directory)>();
        var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var type = ((uint)entry.ExternalAttributes >> 16) & 0xF000;
            if (((uint)entry.ExternalAttributes & (uint)FileAttributes.ReparsePoint) != 0 || type != 0 && type is not 0x4000 and not 0x8000)
                throw new InvalidDataException("安装载荷禁止包含链接或特殊文件。");
            var directory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            if (type == 0x4000 && !directory || directory && entry.Length != 0) throw new InvalidDataException("安装载荷目录条目无效。");
            if (entry.Length < 0 || entry.Length > MaxEntryBytes || expanded > MaxExpandedBytes - entry.Length) throw new InvalidDataException("安装载荷解压大小超限。");
            expanded += entry.Length;
            var path = Resolve(entry.FullName);
            if (!paths.TryAdd(path, directory)) throw new InvalidDataException("安装载荷包含重复或别名文件路径。");
            plans.Add((entry, path, directory));
        }
        // Reject a file that is also the parent of another entry before writing anything.
        foreach (var plan in plans)
        {
            for (var cursor = Path.GetDirectoryName(plan.Path); cursor != null && !cursor.Equals(Root, StringComparison.OrdinalIgnoreCase); cursor = Path.GetDirectoryName(cursor))
                if (paths.TryGetValue(cursor, out var isDirectory) && !isDirectory) throw new InvalidDataException("安装载荷文件与目录冲突。");
        }
        foreach (var plan in plans)
        {
            EnsureDirectory(plan.Directory ? plan.Path : Path.GetDirectoryName(plan.Path)!);
            if (plan.Directory) continue;
            EnsureNoReparse(plan.Path);
            var attributes = SecurityAttributes();
            using (var handle = Native.CreateFile(plan.Path, 0x40000000, 0, ref attributes, 1, 0x80, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建安装载荷文件。");
                using var output = new FileStream(handle, FileAccess.Write, 131072, false);
                using var input = plan.Entry.Open();
                var buffer = new byte[131072];
                long written = 0;
                int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (written > plan.Entry.Length - count) throw new InvalidDataException("安装载荷实际长度与清单不符。");
                    output.Write(buffer, 0, count); written += count;
                }
                if (written != plan.Entry.Length) throw new InvalidDataException("安装载荷文件不完整。");
                output.Flush(true);
            }
            VerifyObject(plan.Path, false);
        }
        VerifyTree();
    }

    private void EnsureDirectory(string path)
    {
        if (directoryLocks.ContainsKey(path)) { VerifyObject(path, true); return; }
        if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new SecurityException("无效的安装子目录。");
        EnsureDirectory(Path.GetDirectoryName(path)!);
        EnsureNoReparse(path);
        var attributes = SecurityAttributes();
        if (!Native.CreateDirectory(path, ref attributes)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建独占安装子目录。");
        directoryLocks.Add(path, OpenDirectoryLock(path));
        VerifyObject(path, true);
    }

    private Native.SecurityAttributes SecurityAttributes() => new() { Length = Marshal.SizeOf<Native.SecurityAttributes>(), Descriptor = descriptor, InheritHandle = false };

    private void LockAncestors()
    {
        EnsureNoReparse(parent);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("安装临时目录的父目录不存在。");
        var cursor = Path.GetPathRoot(parent)!;
        ancestorLocks.Add(OpenDirectoryLock(cursor));
        foreach (var segment in parent[cursor.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            cursor = Path.Combine(cursor, segment);
            ancestorLocks.Add(OpenDirectoryLock(cursor));
        }
    }

    private static SafeFileHandle OpenDirectoryLock(string directory)
    {
        var attributes = new Native.SecurityAttributes { Length = Marshal.SizeOf<Native.SecurityAttributes>() };
        // No write/delete sharing: neither rename nor FSCTL_SET_REPARSE_POINT can
        // redirect a directory while payload paths are used by this process.
        // FILE_LIST_DIRECTORY is necessary: metadata-only opens do not participate
        // in Windows data/delete sharing checks and would not block rename.
        var handle = Native.CreateFile(directory, 0x01 | 0x80 | 0x20000, 1, ref attributes, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, "无法锁定安装目录身份。"); }
        if (!Native.GetFileInformationByHandle(handle, out var information)) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        if ((information.Attributes & (uint)FileAttributes.Directory) == 0 || (information.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
        { handle.Dispose(); throw new SecurityException("安装路径包含目录重定向。"); }
        return handle;
    }

    private static void EnsureNoReparse(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith("\\\\", StringComparison.Ordinal) || full.StartsWith("\\\\?", StringComparison.Ordinal)) throw new SecurityException("安装目录必须位于本地普通路径。");
        var cursor = Path.GetPathRoot(full)!;
        foreach (var segment in full[cursor.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            cursor = Path.Combine(cursor, segment);
            try { if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new SecurityException("安装路径包含链接或重定向。"); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private void VerifyObject(string path, bool directory)
    {
        EnsureNoReparse(path);
        if (!protectedMode) return;
        FileSystemSecurity security = directory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (!security.AreAccessRulesProtected || !Administrators.Equals(security.GetOwner(typeof(SecurityIdentifier)))) throw new SecurityException("安装载荷目录或文件的所有者/继承策略不正确。");
        var principals = new HashSet<string>(StringComparer.Ordinal);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || rule.IsInherited || rule.IdentityReference.Value != Administrators.Value && rule.IdentityReference.Value != SystemIdentity.Value
                || (rule.FileSystemRights & FileSystemRights.FullControl) != FileSystemRights.FullControl)
                throw new SecurityException("安装载荷目录或文件允许了不受信任的访问。");
            principals.Add(rule.IdentityReference.Value);
        }
        if (principals.Count != 2) throw new SecurityException("安装载荷的管理员与系统权限不完整。");
    }

    public void VerifyTree()
    {
        VerifyIdentity();
        var pending = new Stack<string>(); pending.Push(Root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop(); VerifyObject(directory, true);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new SecurityException("安装临时目录出现链接，已拒绝继续。");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry); else VerifyObject(entry, false);
            }
        }
    }

    private void VerifyIdentity()
    {
        if (!owned || !Path.GetDirectoryName(Root)!.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(Root).StartsWith(Prefix, StringComparison.Ordinal) || !Guid.TryParseExact(Path.GetFileName(Root)[Prefix.Length..], "N", out _))
            throw new SecurityException("无法证明临时目录属于本次安装。");
        EnsureNoReparse(Root);
    }

    public void Dispose()
    {
        if (disposed) return;
        try
        {
            if (owned && Directory.Exists(Root))
            {
                VerifyTree();
                DeleteContents(Root);
                directoryLocks[Root].Dispose(); directoryLocks.Remove(Root);
                EnsureNoReparse(Root);
                Directory.Delete(Root, false);
            }
        }
        finally
        {
            foreach (var handle in directoryLocks.Values) handle.Dispose();
            directoryLocks.Clear();
            foreach (var handle in ancestorLocks.AsEnumerable().Reverse()) handle.Dispose();
            ancestorLocks.Clear();
            if (descriptor != IntPtr.Zero) { Native.LocalFree(descriptor); descriptor = IntPtr.Zero; }
            disposed = true;
        }
    }

    private void DeleteContents(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory).ToArray())
        {
            EnsureNoReparse(entry);
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new SecurityException("清理时发现目录重定向，已停止删除。");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (!directoryLocks.ContainsKey(entry)) directoryLocks.Add(entry, OpenDirectoryLock(entry));
                DeleteContents(entry);
                directoryLocks[entry].Dispose(); directoryLocks.Remove(entry);
                EnsureNoReparse(entry);
                Directory.Delete(entry, false);
            }
            else { VerifyObject(entry, false); File.Delete(entry); }
        }
    }
}

internal static class Native
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int MessageBox(IntPtr handle, string text, string caption, uint type);

    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateDirectory(string path, ref SecurityAttributes attributes);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFile(string path, uint desiredAccess, uint shareMode, ref SecurityAttributes attributes, uint creationDisposition, uint flags, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr LocalFree(IntPtr memory);
}
