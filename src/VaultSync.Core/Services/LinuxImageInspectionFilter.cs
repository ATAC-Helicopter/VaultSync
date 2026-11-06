using System.Runtime.InteropServices;

namespace VaultSync.Core.Services;

// Linux x64 classic BPF, not an extensible policy supplied by images/callers.
// Default denies unknown syscalls, including future filesystem/network interfaces.
internal static class LinuxImageInspectionFilter
{
    internal const uint Allow = 0x7fff0000, Deny = 0x50001, Kill = 0x80000000;
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Instruction(ushort Code, byte True, byte False, uint Value);

    internal static Instruction[] Build(int process, int peer)
    {
        if (process <= 1 || peer < 3 || peer >= 128) throw new InvalidDataException("inspection.filter-identity");
        var code = new List<Instruction>
        {
            new(0x20,0,0,4), new(0x15,1,0,0xc000003e), new(6,0,0,Kill), new(0x20,0,0,0)
        };
        // Existing descriptors/memory, clocks, synchronization and private runtime
        // event/pipe descriptors only. No path, device, network or foreign-memory opens.
        uint[] simple = [0,1,3,5,7,8,9,10,11,12,13,14,15,17,19,20,22,23,24,25,27,28,
            32,33,34,35,36,37,38,39,60,63,96,97,98,99,100,102,104,107,108,110,
            127,128,130,131,186,202,204,218,228,229,230,231,232,233,270,271,273,
            281,283,284,286,287,290,291,292,293,318,324,334,436,449];
        foreach (uint number in simple) { code.Add(new(0x15,0,1,number)); code.Add(new(6,0,0,Allow)); }
        // Force pthread's auditable clone fallback; pointer-based clone3 cannot
        // have its flags inspected by classic BPF.
        code.Add(new(0x15,0,1,435)); code.Add(new(6,0,0,0x50026)); // ENOSYS
        Add(code,56,b => { b.Equal(20,0); b.Mask(16,0x10900,0x10900); b.Mask(16,~0x13d0f00u,0); });
        // CLR initializes each new thread's CPU affinity. PID zero means only
        // the calling thread; never permit changing another task's affinity.
        Add(code,203,b => { b.Equal(20,0); b.Equal(16,0); });
        foreach (uint number in new uint[] {62,234}) Add(code,number,b => { b.Equal(20,0); b.Equal(16,(uint)process); });
        foreach (uint number in new uint[] {44,45,46,47,48,51,52}) Add(code,number,b => { b.Equal(20,0); b.Equal(16,(uint)peer); });
        Add(code,55,b =>
        {
            b.Equal(20,0); b.Equal(16,(uint)peer); b.Equal(28,0); b.Equal(24,1); b.Equal(36,0);
            b.OneOf(32,[3,4,17]); // SO_TYPE, SO_ERROR, SO_PEERCRED; never SO_PEERPIDFD.
        });
        Add(code,16,b => { b.Equal(20,0); b.Equal(16,(uint)peer); b.Equal(28,0); b.Equal(24,0x541b); });
        Add(code,72,b => { b.Equal(28,0); b.OneOf(24,[0,1,2,3,4,1030]); });
        Add(code,157,b => { b.Equal(20,0); b.Equal(16,15); }); // PR_SET_NAME only.
        Add(code,158,b => { b.Equal(20,0); b.OneOf(16,[0x1002,0x1003,0x1004]); });
        code.Add(new(6,0,0,Deny));
        return code.ToArray();
    }

    private static void Add(List<Instruction> code, uint number, Action<Conditions> configure)
    {
        var conditions = new Conditions(); configure(conditions);
        Instruction[] body = conditions.Finish();
        code.Add(new(0x15,0,checked((byte)body.Length),number)); code.AddRange(body);
    }

    private sealed class Conditions
    {
        private readonly List<Instruction> code = new();
        private readonly List<int> failures = new();
        internal void Equal(uint offset,uint value) => Mask(offset,uint.MaxValue,value);
        internal void Mask(uint offset,uint mask,uint value)
        {
            code.Add(new(0x20,0,0,offset));
            if (mask != uint.MaxValue) code.Add(new(0x54,0,0,mask));
            failures.Add(code.Count); code.Add(new(0x15,0,0,value));
        }
        internal void OneOf(uint offset,uint[] values)
        {
            code.Add(new(0x20,0,0,offset));
            for (int n=0;n<values.Length;n++) code.Add(new(0x15,checked((byte)(values.Length-n)),0,values[n]));
            code.Add(new(6,0,0,Deny));
        }
        internal Instruction[] Finish()
        {
            code.Add(new(6,0,0,Allow)); int failed=code.Count; code.Add(new(6,0,0,Deny));
            foreach(int index in failures) code[index]=code[index] with { False=checked((byte)(failed-index-1)) };
            return code.ToArray();
        }
    }
}
