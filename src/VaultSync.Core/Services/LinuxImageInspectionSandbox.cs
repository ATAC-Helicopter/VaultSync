using System.Globalization;
using System.Runtime.InteropServices;

namespace VaultSync.Core.Services;

/// <summary>Irreversible child-only Linux x64 policy. Never enter in the app/broker.</summary>
internal static class LinuxImageInspectionSandbox
{
    internal const uint Nobody=65534;
    private const ulong MiB=1024*1024;
    internal static uint User => GetUser();
    internal static uint Group => GetGroup();

    internal static void Enter(int peer)
    {
        if (!LinuxInventoryWorkerPeerChannel.Supported) throw new PlatformNotSupportedException();
        // Inspect trusted runtime descriptors while its installation is still
        // traversable. No image bytes have been requested at this point.
        ValidateDescriptors(peer);
        uint user=User,group=Group;
        if (user==0)
        {
            // libc/NPTL wrappers synchronize credentials across existing threads.
            Require(SetGroups(0,IntPtr.Zero));
            Require(SetGroup(Nobody,Nobody,Nobody)); Require(SetUser(Nobody,Nobody,Nobody));
            user=group=Nobody;
        }
        if(user==0 || group==0) throw new InvalidDataException("inspection.privileged-identity");
        Require(Prctl(4,0,0,0,0)); // PR_SET_DUMPABLE=0, process-wide.
        Require(Prctl(38,1,0,0,0)); // PR_SET_NO_NEW_PRIVS.
        ValidateProcess(Environment.ProcessId,user,group,false);
        string status=File.ReadAllText("/proc/self/status");
        var limits=MemoryLimits(StatusKiB(status,"VmSize"),StatusKiB(status,"VmData"));
        Limit(0,120); Limit(1,0); Limit(2,limits.Data); Limit(4,0); Limit(7,128); Limit(9,limits.Address);
        var code=LinuxImageInspectionFilter.Build(Environment.ProcessId,peer);
        var pinned=GCHandle.Alloc(code,GCHandleType.Pinned);
        try
        {
            var program=new Program { Count=checked((ushort)code.Length), Instructions=pinned.AddrOfPinnedObject() };
            // Any nonzero return (including an unsynchronized TID) fails closed.
            if(Seccomp(317,1,1,ref program)!=0) throw new InvalidDataException("inspection.filter-unavailable");
        }
        finally { pinned.Free(); }
    }

    private static ulong StatusKiB(string text,string key) => ulong.Parse(text.Split('\n')
        .Single(s=>s.StartsWith(key+":",StringComparison.Ordinal))
        .Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries)[1],CultureInfo.InvariantCulture);

    // Account for the CLR's existing reservations before lowering limits. These
    // are virtual/data allocation bounds, not cgroup resident-memory containment.
    internal static (ulong Address,ulong Data) MemoryLimits(ulong virtualKiB,ulong dataKiB)
    {
        const ulong addressMaximum=16*1024*MiB,dataMaximum=2*1024*MiB;
        if(virtualKiB==0 || dataKiB==0 || virtualKiB>(addressMaximum-2*1024*MiB)/1024 ||
            dataKiB>(dataMaximum-512*MiB)/1024)
            throw new InvalidDataException("inspection.memory-baseline");
        return (virtualKiB*1024+2*1024*MiB,dataKiB*1024+512*MiB);
    }

    internal static void ValidateProcess(int process,uint user,uint group,bool filtered) =>
        ValidateThreads(user,group,filtered,
            ()=>Directory.GetDirectories("/proc/"+process.ToString(CultureInfo.InvariantCulture)+"/task"),
            task=>File.ReadAllText(Path.Combine(task,"status")));

    // Kernel tasks can retire while a directory snapshot is read. Require a
    // complete stable before/after set, never silently skip a missing thread.
    internal static void ValidateThreads(uint user,uint group,bool filtered,Func<string[]> list,Func<string,string> read)
    {
        for(int attempt=0;attempt<3;attempt++)
        {
            string[] tasks=list().Order(StringComparer.Ordinal).ToArray();
            if(tasks.Length is 0 or >256 || tasks.Distinct(StringComparer.Ordinal).Count()!=tasks.Length)
                throw new InvalidDataException("inspection.thread-limit");
            try
            {
                if(tasks.Any(task=>!StatusMatches(read(task),user,group,filtered)))
                    throw new InvalidDataException("inspection.privilege-state");
            }
            catch(Exception error) when(error is FileNotFoundException or DirectoryNotFoundException) { continue; }
            if(tasks.SequenceEqual(list().Order(StringComparer.Ordinal),StringComparer.Ordinal)) return;
        }
        throw new InvalidDataException("inspection.changed-threads");
    }

    private static bool StatusMatches(string text,uint user,uint group,bool filtered)
    {
        if(text.Length>65536) return false;
        var fields=text.Split('\n').Where(s=>s.Contains(':')).Select(s=>s.Split(':',2))
            .ToDictionary(s=>s[0],s=>s[1].Trim(),StringComparer.Ordinal);
        return Ids(fields,"Uid",user) && Ids(fields,"Gid",group) &&
            new[]{"CapInh","CapPrm","CapEff","CapAmb"}.All(k=>fields.TryGetValue(k,out string? v) &&
                ulong.TryParse(v,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out ulong caps) && caps==0) &&
            (!filtered || fields.GetValueOrDefault("NoNewPrivs")=="1" && fields.GetValueOrDefault("Seccomp")=="2");
    }

    private static bool Ids(Dictionary<string,string> fields,string key,uint expected) => expected!=0 &&
        fields.TryGetValue(key,out string? value) && value.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries) is {Length:4} ids &&
        ids.All(s=>uint.TryParse(s,NumberStyles.None,CultureInfo.InvariantCulture,out uint id) && id==expected);

    private static void ValidateDescriptors(int peer)
    {
        foreach(string path in Directory.GetFiles("/proc/self/fd"))
        {
            int fd=int.Parse(Path.GetFileName(path),CultureInfo.InvariantCulture);
            int flags=Fcntl(fd,3);
            if(flags<0) continue; // enumeration's own already-closed descriptor
            string link=File.ResolveLinkTarget(path,false)?.FullName ?? throw new InvalidDataException("inspection.fd-link");
            if(!AllowedDescriptor(fd,flags,link,peer)) throw new InvalidDataException("inspection.inherited-descriptor");
        }
    }

    private static bool AllowedDescriptor(int fd,int flags,string link,int peer)
    {
        string name=Path.GetFileName(link);
        return fd==peer || name is "anon_inode:[eventpoll]" or "anon_inode:[eventfd]" or "anon_inode:[pidfd]" ||
            name.StartsWith("pipe:[",StringComparison.Ordinal) ||
            link=="/memfd:doublemapper (deleted)" && AnonymousJitMemory(fd) ||
            (flags&3)==0 && (link.StartsWith(AppContext.BaseDirectory,StringComparison.Ordinal) ||
                link.StartsWith(RuntimeEnvironment.GetRuntimeDirectory(),StringComparison.Ordinal)) && File.Exists(link) ||
            link=="/dev/urandom" && (flags&3)==0;
    }

    private static bool AnonymousJitMemory(int fd) => (Fcntl(fd,1)&1)!=0 &&
        Statx(fd,"",0x1100,0x7ff,out var status)==0 && (status.Mask&15)==15 &&
        (status.Mode&0xf000)==0x8000 && status.Links==0 && status.OwnerUid==User &&
        Statfs(fd,out var filesystem)==0 && filesystem.Type==0x01021994;

    private static void Limit(int resource,ulong maximum)
    {
        Require(GetLimit(resource,out var old)); ulong bound=Math.Min(old.Maximum,maximum);
        var value=new LimitValue { Current=Math.Min(old.Current,bound), Maximum=bound }; Require(SetLimit(resource,in value));
    }
    private static void Require(int result) { if(result!=0) throw new InvalidDataException("inspection.sandbox-unavailable"); }
    [StructLayout(LayoutKind.Sequential)] private struct Program { public ushort Count; public IntPtr Instructions; }
    [StructLayout(LayoutKind.Sequential)] private struct LimitValue { public ulong Current,Maximum; }
    [StructLayout(LayoutKind.Explicit,Size=256)] private struct FileStatus
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint Links;
        [FieldOffset(20)] public uint OwnerUid;
        [FieldOffset(28)] public ushort Mode;
    }
    [StructLayout(LayoutKind.Explicit,Size=120)] private struct FilesystemStatus { [FieldOffset(0)] public long Type; }
    [DllImport("libc",EntryPoint="statx",CallingConvention=CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern int Statx(int fd,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,int flags,uint mask,out FileStatus status);
    [DllImport("libc",EntryPoint="fstatfs",CallingConvention=CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern int Statfs(int fd,out FilesystemStatus status);
    [DllImport("libc",EntryPoint="getuid",CallingConvention=CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern uint GetUser();
    [DllImport("libc",EntryPoint="getgid",CallingConvention=CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern uint GetGroup();
    [DllImport("libc",EntryPoint="setresuid",CallingConvention=CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern int SetUser(uint real,uint effective,uint saved);
    [DllImport("libc",EntryPoint="setresgid",CallingConvention=CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern int SetGroup(uint real,uint effective,uint saved);
    [DllImport("libc",EntryPoint="setgroups",CallingConvention=CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern int SetGroups(nuint count,IntPtr groups);
    [DllImport("libc",EntryPoint="prctl",CallingConvention=CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern int Prctl(int option,ulong a,ulong b,ulong c,ulong d);
    [DllImport("libc",EntryPoint="fcntl",CallingConvention=CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern int Fcntl(int fd,int command);
    [DllImport("libc",EntryPoint="getrlimit",CallingConvention=CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern int GetLimit(int resource,out LimitValue value);
    [DllImport("libc",EntryPoint="setrlimit",CallingConvention=CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern int SetLimit(int resource,in LimitValue value);
    [DllImport("libc",EntryPoint="syscall",CallingConvention=CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern long Seccomp(long number,int operation,uint flags,ref Program program);
}
