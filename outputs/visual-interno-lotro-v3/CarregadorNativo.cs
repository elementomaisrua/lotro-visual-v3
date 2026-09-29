using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace LotroVisual
{
    // Este perfil acompanha somente a DLL freestanding deste pacote.
    // A imagem permanece no hospedeiro ate ele terminar; nao ha unload.
    public static class NativeLoader
    {
        private const uint SupportedTimestamp = 0x6a947bf8;
        private const uint SupportedImageSize = 0x022bd000;

        public static void Validate(byte[] dllBytes)
        {
            new PreparedImage(dllBytes);
        }

        public static string Load(int pid, string expectedExe, long expectedStartFileTime, byte[] dllBytes)
        {
            if (IntPtr.Size != 8) throw new InvalidOperationException("O carregador precisa executar em 64 bits.");
            if (pid <= 0 || expectedStartFileTime <= 0) throw new ArgumentException("PID e inicio do processo sao obrigatorios.");
            if (String.IsNullOrEmpty(expectedExe) || !Path.IsPathRooted(expectedExe) ||
                !String.Equals(Path.GetFileName(expectedExe), "lotroclient64.exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("O executavel esperado deve ser o caminho completo de lotroclient64.exe.");

            PreparedImage image = new PreparedImage(dllBytes);
            StringBuilder report = new StringBuilder();
            report.AppendLine("Metodo: mapeamento manual PE x64; imagem principal sem LoadLibrary.");
            report.AppendLine("PID: " + pid + "; inicio FILETIME: " + expectedStartFileTime);
            report.AppendLine("DLL SHA256: " + image.Sha256);
            bool ownsGate = false;
            using (Mutex gate = new Mutex(false, "Local\\CodexLotroVisualLoad-" + pid))
            {
                try
                {
                    try { ownsGate = gate.WaitOne(0); }
                    catch (AbandonedMutexException) { ownsGate = true; }
                    if (!ownsGate) throw new InvalidOperationException("Outra carga esta em andamento neste PID.");
                    if (HasVisualMutex(pid)) throw new InvalidOperationException("A DLL visual ja esta ativa neste PID; use ligar/desligar.");
                    return Map(pid, expectedExe, expectedStartFileTime, image, report);
                }
                finally
                {
                    if (ownsGate) gate.ReleaseMutex();
                }
            }
        }

        private static string Map(int pid, string expectedExe, long expectedStartFileTime,
                                  PreparedImage image, StringBuilder report)
        {
            RemoteProcess remote = null;
            ulong imageBase = 0;
            bool registered = false;
            bool unwindUncertain = false;
            bool entryAttempted = false;
            bool completed = false;
            Exception failure = null;
            try
            {
                remote = new RemoteProcess(pid, expectedExe, expectedStartFileTime);
                report.AppendLine("Executavel validado: " + remote.Exe);
                report.AppendLine("Build validada: timestamp 0x6a947bf8, SizeOfImage 0x22bd000.");
                // O mutex do payload e verificado depois de validar o handle, antes de alocar.
                if (HasVisualMutex(pid)) throw new InvalidOperationException("A DLL visual ja esta ativa neste PID.");
                try { imageBase = remote.Allocate(image.Size, image.PreferredBase); }
                catch (Win32Exception)
                {
                    if (image.Directories[5].Rva == 0) throw;
                    imageBase = remote.Allocate(image.Size, 0);
                }
                report.AppendLine("Imagem: 0x" + imageBase.ToString("X") + "; bytes: " + image.Size);
                report.AppendLine("Realocacoes aplicadas: " + image.Relocate(imageBase));
                int imports = 0;
                foreach (ImportEntry entry in image.Imports)
                {
                    ulong address = remote.Resolve(entry.Dll, entry.Name, entry.Ordinal);
                    Write64(image.Data, entry.IatRva, address);
                    imports++;
                }
                report.AppendLine("Imports resolvidos: " + imports);
                remote.Write(imageBase, image.Data);
                remote.Protect(imageBase, image.Size, 0x01); // PAGE_NOACCESS para lacunas.
                remote.Protect(imageBase, image.Headers, 0x02);
                foreach (Section section in image.Sections)
                {
                    if (section.Size == 0) continue;
                    bool execute = (section.Flags & 0x20000000) != 0;
                    bool read = (section.Flags & 0x40000000) != 0;
                    bool write = (section.Flags & 0x80000000) != 0;
                    uint protection = execute ? (write ? 0x40u : read ? 0x20u : 0x10u) :
                                                (write ? 0x04u : read ? 0x02u : 0x01u);
                    remote.Protect(imageBase + section.Rva, section.Size, protection);
                    report.AppendLine("Secao " + section.Name + ": protecao 0x" + protection.ToString("X"));
                }
                remote.Flush(imageBase, image.Size);
                DirectoryEntry pdata = image.Directories[3];
                if (pdata.Rva != 0)
                {
                    ulong addFunctionTable = remote.Resolve("kernel32.dll", "RtlAddFunctionTable", null);
                    // Em caso de retorno desconhecido, nao podemos descartar uma tabela ainda registrada.
                    unwindUncertain = true;
                    ulong added = remote.Call(addFunctionTable, imageBase + pdata.Rva, pdata.Size / 12, imageBase);
                    unwindUncertain = false;
                    if ((added & 0xff) == 0) throw new InvalidOperationException("Registro de unwind recusado.");
                    registered = true;
                }
                report.AppendLine("Unwind registrado: " + registered);
                entryAttempted = true;
                ulong result = remote.Call(imageBase + image.Entry, imageBase, 1, 0);
                if ((result & 0xffffffff) == 0) throw new InvalidOperationException("DllMain retornou FALSE.");
                report.AppendLine("DllMain retornou TRUE; inicializacao da janela ocorre na thread do payload.");
                // Mantem a exclusao entre carregadores ate o worker assumir seu proprio mutex.
                bool workerObserved = false;
                for (int i = 0; i < 120; i++)
                {
                    if (HasVisualMutex(pid)) { workerObserved = true; break; }
                    remote.EnsureAlive();
                    Thread.Sleep(25);
                }
                report.AppendLine("Mutex do worker observado: " + workerObserved);
                completed = true;
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (remote != null)
                {
                    // Nunca remove a imagem depois de tentar DllMain: ele pode ter criado uma thread.
                    if (imageBase != 0 && !completed && !entryAttempted && !unwindUncertain && !remote.InFlight)
                    {
                        if (registered)
                        {
                            try
                            {
                                ulong deleted = remote.Call(remote.Resolve("kernel32.dll", "RtlDeleteFunctionTable", null),
                                                            imageBase + image.Directories[3].Rva);
                                registered = (deleted & 0xff) == 0;
                                if (registered) report.AppendLine("Remocao da tabela unwind recusada; imagem preservada.");
                            }
                            catch (Exception cleanupError)
                            {
                                registered = true;
                                report.AppendLine("Tabela unwind preservada: " + cleanupError.Message);
                            }
                        }
                        if (!registered && !remote.InFlight)
                        {
                            string releaseError = remote.TryFree(imageBase);
                            if (releaseError == null)
                            {
                                report.AppendLine("Imagem liberada apos falha anterior a inicializacao.");
                                imageBase = 0;
                            }
                            else report.AppendLine(releaseError);
                        }
                    }
                    if (imageBase != 0)
                        report.AppendLine("Imagem preservada ate o processo encerrar em 0x" + imageBase.ToString("X") + ".");
                    remote.Close(report);
                }
            }
            if (failure != null)
            {
                report.AppendLine("Falha: " + failure.Message);
                if (entryAttempted || unwindUncertain || (remote != null && remote.InFlight))
                    report.AppendLine("Nao repita a carga nesta instancia se o estado for incerto; reinicie o jogo antes.");
                throw new InvalidOperationException(report.ToString(), failure);
            }
            report.AppendLine("Estado: imagem mapeada e DllMain concluido. Confira Ready/status para validar os circulos.");
            return report.ToString();
        }

        private static bool HasVisualMutex(int pid)
        {
            IntPtr handle = Native.OpenMutexW(0x00100000, false, "Local\\CodexLotroVisual-" + pid);
            if (handle != IntPtr.Zero)
            {
                if (!Native.CloseHandle(handle)) throw Win32("Fechar verificacao do mutex visual");
                return true;
            }
            int error = Marshal.GetLastWin32Error();
            if (error == 2) return false;
            throw Win32("Verificar mutex visual", error);
        }

        private static Win32Exception Win32(string operation)
        {
            return Win32(operation, Marshal.GetLastWin32Error());
        }

        private static Win32Exception Win32(string operation, int error)
        {
            return new Win32Exception(error, operation + ": Win32 " + error + " (" + new Win32Exception(error).Message + ").");
        }

        private static void Need(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
        }

        private static IntPtr Pointer(ulong address)
        {
            return new IntPtr(unchecked((long)address));
        }

        private static ulong Address(IntPtr pointer)
        {
            return unchecked((ulong)pointer.ToInt64());
        }

        private static void Bounds(byte[] bytes, ulong offset, ulong count)
        {
            Need(offset <= (ulong)bytes.Length && count <= (ulong)bytes.Length - offset, "PE: leitura ou escrita fora dos limites.");
        }

        private static ushort U16(byte[] bytes, ulong offset)
        {
            Bounds(bytes, offset, 2);
            return BitConverter.ToUInt16(bytes, checked((int)offset));
        }

        private static uint U32(byte[] bytes, ulong offset)
        {
            Bounds(bytes, offset, 4);
            return BitConverter.ToUInt32(bytes, checked((int)offset));
        }

        private static ulong U64(byte[] bytes, ulong offset)
        {
            Bounds(bytes, offset, 8);
            return BitConverter.ToUInt64(bytes, checked((int)offset));
        }

        private static void Write64(byte[] bytes, ulong offset, ulong value)
        {
            Bounds(bytes, offset, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, checked((int)offset), 8);
        }

        private sealed class DirectoryEntry { public uint Rva; public uint Size; }
        private sealed class Section { public string Name; public uint Rva; public uint Size; public uint Flags; }
        private sealed class ImportEntry { public string Dll; public string Name; public ushort? Ordinal; public uint IatRva; }

        private sealed class PreparedImage
        {
            public readonly byte[] Data;
            public readonly uint Size, Headers, Entry;
            public readonly ulong PreferredBase;
            public readonly string Sha256;
            public readonly List<Section> Sections = new List<Section>();
            public readonly List<ImportEntry> Imports = new List<ImportEntry>();
            public readonly DirectoryEntry[] Directories = new DirectoryEntry[16];

            public PreparedImage(byte[] source)
            {
                if (source == null) throw new ArgumentNullException("dllBytes");
                // Copia antes de validar para impedir mudancas concorrentes no array do chamador.
                byte[] raw = (byte[])source.Clone();
                Need(raw.Length >= 64 && U16(raw, 0) == 0x5a4d, "Arquivo PE invalido.");
                ulong pe = U32(raw, 60);
                Bounds(raw, pe, 24);
                Need(U32(raw, pe) == 0x4550, "Assinatura PE invalida.");
                ushort sections = U16(raw, pe + 6);
                ushort optionalSize = U16(raw, pe + 20);
                ulong optional = pe + 24;
                Bounds(raw, optional, optionalSize);
                Need(U16(raw, pe + 4) == 0x8664 && (U16(raw, pe + 22) & 0x2000) != 0 &&
                     optionalSize >= 240 && U16(raw, optional) == 0x20b,
                     "Este carregador aceita apenas a DLL nativa x64 preparada.");
                Need(sections > 0 && sections <= 96 && U32(raw, optional + 108) >= 16,
                     "Cabecalho opcional ou numero de secoes invalido.");
                Entry = U32(raw, optional + 16);
                PreferredBase = U64(raw, optional + 24);
                uint alignment = U32(raw, optional + 32);
                Size = U32(raw, optional + 56);
                Headers = U32(raw, optional + 60);
                Need(alignment >= 4096 && (alignment & (alignment - 1)) == 0, "Alinhamento de secoes nao suportado.");
                Need(Size > 0 && Size <= 32 * 1024 * 1024 && Headers > 0 && Headers <= Size && Headers <= raw.Length,
                     "Tamanho de imagem/cabecalho invalido.");
                Need(PreferredBase >= 0x10000 && PreferredBase <= 0x00007fffffffffffUL - Size,
                     "Endereco preferencial invalido.");
                ulong sectionTable = optional + optionalSize;
                Bounds(raw, sectionTable, (ulong)sections * 40);
                Need(sectionTable + (ulong)sections * 40 <= Headers, "Tabela de secoes fora dos cabecalhos.");
                Data = new byte[checked((int)Size)];
                Buffer.BlockCopy(raw, 0, Data, 0, checked((int)Headers));
                List<ulong[]> pageRanges = new List<ulong[]>();
                pageRanges.Add(new ulong[] { 0, ((ulong)Headers + 4095) & ~4095UL });
                bool executableEntry = false;
                for (uint index = 0; index < sections; index++)
                {
                    ulong offset = sectionTable + index * 40;
                    string name = Encoding.ASCII.GetString(raw, (int)offset, 8).TrimEnd('\0');
                    uint virtualSize = U32(raw, offset + 8);
                    uint rva = U32(raw, offset + 12);
                    uint rawSize = U32(raw, offset + 16);
                    uint rawOffset = U32(raw, offset + 20);
                    uint flags = U32(raw, offset + 36);
                    uint span = Math.Max(virtualSize, rawSize);
                    Need(rva % alignment == 0, "Secao com alinhamento invalido: " + name);
                    Bounds(Data, rva, span);
                    if (rawSize != 0) Bounds(raw, rawOffset, rawSize);
                    ulong end = ((ulong)rva + span + 4095) & ~4095UL;
                    Need(end <= Size || span == 0, "Paginas da secao fora da imagem.");
                    if (span != 0)
                    {
                        foreach (ulong[] range in pageRanges)
                            Need(rva >= range[1] || range[0] >= end, "Secoes compartilham paginas; perfil nao suportado.");
                        pageRanges.Add(new ulong[] { rva, end });
                    }
                    if (rawSize != 0) Buffer.BlockCopy(raw, checked((int)rawOffset), Data, checked((int)rva), checked((int)rawSize));
                    Sections.Add(new Section { Name = name, Rva = rva, Size = span, Flags = flags });
                    if (Entry >= rva && (ulong)Entry < (ulong)rva + span && (flags & 0x20000000) != 0) executableEntry = true;
                }
                Need(executableEntry, "Entry point nao esta numa secao executavel.");
                for (uint index = 0; index < 16; index++)
                {
                    uint rva = U32(raw, optional + 112 + index * 8);
                    uint size = U32(raw, optional + 116 + index * 8);
                    Need((rva == 0) == (size == 0), "Diretorio PE incompleto.");
                    if (rva != 0) Bounds(index == 4 ? raw : Data, rva, size);
                    Directories[index] = new DirectoryEntry { Rva = rva, Size = size };
                }
                foreach (int index in new int[] { 9, 10, 13, 14 })
                    Need(Directories[index].Rva == 0 && Directories[index].Size == 0,
                         "TLS, load-config/CFG, delay imports e .NET nao sao suportados pelo perfil preparado.");
                Need(Directories[3].Size % 12 == 0, "Tabela unwind invalida.");
                ReadImports();
                Need(Imports.Count > 0, "O perfil requer imports apenas de KERNEL32.dll ja carregada.");
                ValidateRelocations();
                using (SHA256 sha = SHA256.Create())
                    Sha256 = BitConverter.ToString(sha.ComputeHash(raw)).Replace("-", "").ToLowerInvariant();
            }

            private string ReadString(ulong rva)
            {
                Bounds(Data, rva, 1);
                ulong limit = Math.Min((ulong)Data.Length, rva + 4096);
                for (ulong end = rva; end < limit; end++)
                {
                    byte current = Data[(int)end];
                    if (current == 0) return Encoding.ASCII.GetString(Data, (int)rva, (int)(end - rva));
                    Need(current < 128, "Nome PE deve ser ASCII.");
                }
                throw new InvalidDataException("String PE sem terminacao.");
            }

            private void ReadImports()
            {
                DirectoryEntry directory = Directories[1];
                if (directory.Rva == 0) return;
                ulong end = (ulong)directory.Rva + directory.Size;
                for (ulong offset = directory.Rva; offset + 20 <= end; offset += 20)
                {
                    uint original = U32(Data, offset), name = U32(Data, offset + 12), first = U32(Data, offset + 16);
                    if ((original | U32(Data, offset + 4) | U32(Data, offset + 8) | name | first) == 0) return;
                    string dll = ReadString(name);
                    Need(String.Equals(dll, "kernel32.dll", StringComparison.OrdinalIgnoreCase), "Dependencia nao permitida: " + dll);
                    Need(name != 0 && first != 0, "Descritor de import invalido.");
                    bool terminated = false;
                    for (uint index = 0; index < 4096; index++)
                    {
                        ulong sourceRva = (ulong)(original == 0 ? first : original) + index * 8;
                        ulong targetRva = (ulong)first + index * 8;
                        Bounds(Data, targetRva, 8);
                        ulong value = U64(Data, sourceRva);
                        if (value == 0) { terminated = true; break; }
                        ImportEntry import = new ImportEntry { Dll = dll, IatRva = checked((uint)targetRva) };
                        if ((value & 0x8000000000000000UL) != 0)
                        {
                            Need((value & 0x7fffffffffff0000UL) == 0, "Ordinal PE invalido.");
                            import.Ordinal = (ushort)(value & 0xffff);
                        }
                        else
                        {
                            Bounds(Data, value, 2);
                            import.Name = ReadString(value + 2);
                            Need(import.Name.Length > 0, "Nome de import vazio.");
                        }
                        Imports.Add(import);
                    }
                    Need(terminated, "Tabela de imports sem terminacao.");
                }
                throw new InvalidDataException("Descritores de import sem terminacao.");
            }

            private void ValidateRelocations()
            {
                VisitRelocations(0, false);
            }

            public int Relocate(ulong address)
            {
                if (address == PreferredBase) return 0;
                Need(Directories[5].Rva != 0, "Imagem sem realocacoes; endereco preferencial indisponivel.");
                return VisitRelocations(unchecked(address - PreferredBase), true);
            }

            private int VisitRelocations(ulong delta, bool apply)
            {
                DirectoryEntry directory = Directories[5];
                ulong end = (ulong)directory.Rva + directory.Size;
                int count = 0;
                for (ulong offset = directory.Rva; offset < end; )
                {
                    Need(offset + 8 <= end, "Cabecalho de realocacao truncado.");
                    uint page = U32(Data, offset), block = U32(Data, offset + 4);
                    Need(block >= 8 && block % 2 == 0 && block <= end - offset, "Bloco de realocacao invalido.");
                    for (ulong itemOffset = offset + 8; itemOffset < offset + block; itemOffset += 2)
                    {
                        ushort item = U16(Data, itemOffset);
                        int kind = item >> 12;
                        if (kind == 0) continue;
                        Need(kind == 10, "Realocacao diferente de DIR64 nao suportada.");
                        ulong target = (ulong)page + (uint)(item & 0xfff);
                        Bounds(Data, target, 8);
                        if (apply) Write64(Data, target, unchecked(U64(Data, target) + delta));
                        count++;
                    }
                    offset += block;
                }
                return count;
            }
        }

        private sealed class ModuleInfo
        {
            public string Name, Path;
            public ulong Base;
            public uint Size;
        }

        private sealed class RemoteProcess
        {
            // Win64: funcao, RCX, RDX, R8, R9, retorno e flag de conclusao.
            // Mesmo thunk de chamada_x64.asm/manual_map.py; ate quatro argumentos.
            private static readonly byte[] Thunk = new byte[] {
                0x48,0x83,0xec,0x28,0x48,0x89,0x4c,0x24,0x20,0x48,0x8b,0x01,
                0x48,0x8b,0x51,0x10,0x4c,0x8b,0x41,0x18,0x4c,0x8b,0x49,0x20,
                0x48,0x8b,0x49,0x08,0xff,0xd0,0x4c,0x8b,0x54,0x24,0x20,0x49,
                0x89,0x42,0x28,0x49,0xc7,0x42,0x30,0x01,0x00,0x00,0x00,0x31,
                0xc0,0x48,0x83,0xc4,0x28,0xc3
            };
            private IntPtr handle;
            private ulong code, parameters;
            private readonly List<ModuleInfo> modules;
            private readonly Dictionary<string, ulong> resolved = new Dictionary<string, ulong>();
            public string Exe { get; private set; }
            public bool InFlight { get; private set; }

            public RemoteProcess(int pid, string expectedExe, long expectedStart)
            {
                // Consulta, sincronizacao, VM read/write/operation e criacao de thread.
                handle = Native.OpenProcess(0x0010043a, false, (uint)pid);
                if (handle == IntPtr.Zero) throw Win32("Abrir lotroclient64.exe");
                try
                {
                    Native.FileTime creation, exit, kernel, user;
                    if (!Native.GetProcessTimes(handle, out creation, out exit, out kernel, out user)) throw Win32("Conferir inicio do processo");
                    ulong actualStart = ((ulong)creation.High << 32) | creation.Low;
                    if (actualStart != (ulong)expectedStart)
                        throw new InvalidOperationException("O PID foi reutilizado ou o jogo reiniciou. Selecione a instancia atual.");
                    StringBuilder path = new StringBuilder(32768);
                    uint pathLength = (uint)path.Capacity;
                    if (!Native.QueryFullProcessImageNameW(handle, 0, path, ref pathLength)) throw Win32("Identificar executavel do PID");
                    Exe = path.ToString();
                    if (!String.Equals(Path.GetFileName(Exe), "lotroclient64.exe", StringComparison.OrdinalIgnoreCase) ||
                        !String.Equals(Path.GetFullPath(Exe), Path.GetFullPath(expectedExe), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("O PID nao pertence ao lotroclient64.exe informado.");
                    ushort machine, nativeMachine;
                    if (!Native.IsWow64Process2(handle, out machine, out nativeMachine)) throw Win32("Conferir arquitetura do alvo");
                    if (machine != 0 || nativeMachine != 0x8664) throw new InvalidOperationException("O alvo precisa ser x64 nativo.");
                    EnsureAlive();
                    modules = ReadModules(pid);
                    ValidateMainImage();
                }
                catch
                {
                    Native.CloseHandle(handle);
                    handle = IntPtr.Zero;
                    throw;
                }
            }

            public void EnsureAlive()
            {
                uint state = Native.WaitForSingleObject(handle, 0);
                if (state == 0xffffffff) throw Win32("Conferir processo ativo");
                if (state != 258) throw new InvalidOperationException("O jogo encerrou durante o carregamento.");
            }

            private void ValidateMainImage()
            {
                ModuleInfo main = null;
                foreach (ModuleInfo module in modules)
                    if (String.Equals(module.Name, "lotroclient64.exe", StringComparison.OrdinalIgnoreCase) &&
                        String.Equals(module.Path, Exe, StringComparison.OrdinalIgnoreCase))
                    {
                        if (main != null) throw new InvalidOperationException("Modulo principal ambiguo.");
                        main = module;
                    }
                if (main == null) throw new InvalidOperationException("Modulo principal LOTRO nao encontrado.");
                // Conferir o PE carregado vincula a build ao mesmo handle, sem confiar no arquivo em disco.
                byte[] dos = Read(main.Base, 64);
                uint peOffset = U32(dos, 60);
                Need(U16(dos, 0) == 0x5a4d && peOffset <= 0x100000 && (ulong)peOffset + 88 <= main.Size,
                     "Cabecalho do jogo invalido.");
                byte[] pe = Read(main.Base + peOffset, 88);
                Need(U32(pe, 0) == 0x4550 && U16(pe, 4) == 0x8664 && U16(pe, 24) == 0x20b,
                     "Imagem principal nao e PE x64.");
                uint timestamp = U32(pe, 8), imageSize = U32(pe, 80);
                if (timestamp != SupportedTimestamp || imageSize != SupportedImageSize || main.Size != imageSize)
                    throw new InvalidOperationException("Versao do LOTRO nao suportada: timestamp 0x" + timestamp.ToString("X") +
                        ", SizeOfImage 0x" + imageSize.ToString("X") + ". Esperado 0x6a947bf8 / 0x22bd000.");
            }

            private static List<ModuleInfo> ReadModules(int pid)
            {
                IntPtr snapshot = new IntPtr(-1);
                int error = 0;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    snapshot = Native.CreateToolhelp32Snapshot(0x18, (uint)pid);
                    if (snapshot != new IntPtr(-1)) break;
                    error = Marshal.GetLastWin32Error();
                    if (error != 24) break; // ERROR_BAD_LENGTH: a lista pode mudar durante o snapshot.
                }
                if (snapshot == new IntPtr(-1)) throw Win32("Snapshot dos modulos", error);
                try
                {
                    Native.ModuleEntry entry = new Native.ModuleEntry();
                    entry.Size = (uint)Marshal.SizeOf(typeof(Native.ModuleEntry));
                    if (!Native.Module32FirstW(snapshot, ref entry)) throw Win32("Ler primeiro modulo");
                    List<ModuleInfo> result = new List<ModuleInfo>();
                    do
                    {
                        result.Add(new ModuleInfo { Name = entry.Name, Path = entry.Path, Base = Address(entry.Base), Size = entry.BaseSize });
                        entry.Size = (uint)Marshal.SizeOf(typeof(Native.ModuleEntry));
                    } while (Native.Module32NextW(snapshot, ref entry));
                    error = Marshal.GetLastWin32Error();
                    if (error != 18) throw Win32("Ler lista de modulos", error); // ERROR_NO_MORE_FILES.
                    return result;
                }
                finally { Native.CloseHandle(snapshot); }
            }

            public ulong Allocate(uint bytes, ulong preferred)
            {
                IntPtr result = Native.VirtualAllocEx(handle, Pointer(preferred), new UIntPtr(bytes), 0x3000, 0x04);
                if (result == IntPtr.Zero) throw Win32("VirtualAllocEx (" + bytes + " bytes)");
                return Address(result);
            }

            public void Write(ulong destination, byte[] bytes)
            {
                UIntPtr written;
                if (!Native.WriteProcessMemory(handle, Pointer(destination), bytes, new UIntPtr((uint)bytes.Length), out written))
                    throw Win32("WriteProcessMemory");
                if (written.ToUInt64() != (ulong)bytes.Length) throw new IOException("WriteProcessMemory: escrita parcial.");
            }

            public byte[] Read(ulong source, uint count)
            {
                byte[] bytes = new byte[checked((int)count)];
                UIntPtr read;
                if (!Native.ReadProcessMemory(handle, Pointer(source), bytes, new UIntPtr(count), out read)) throw Win32("ReadProcessMemory");
                if (read.ToUInt64() != count) throw new IOException("ReadProcessMemory: leitura parcial.");
                return bytes;
            }

            public void Protect(ulong address, uint count, uint protection)
            {
                uint previous;
                if (!Native.VirtualProtectEx(handle, Pointer(address), new UIntPtr(count), protection, out previous))
                    throw Win32("VirtualProtectEx (0x" + protection.ToString("X") + ")");
            }

            public void Flush(ulong address, uint count)
            {
                if (!Native.FlushInstructionCache(handle, Pointer(address), new UIntPtr(count))) throw Win32("FlushInstructionCache");
            }

            public ulong Resolve(string dll, string symbol, ushort? ordinal)
            {
                if (!String.Equals(dll, "kernel32.dll", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Import fora do perfil preparado.");
                string key = dll.ToLowerInvariant() + "!" + (ordinal.HasValue ? "#" + ordinal.Value : symbol);
                ulong cached;
                if (resolved.TryGetValue(key, out cached)) return cached;
                IntPtr library = Native.GetModuleHandleW(dll);
                if (library == IntPtr.Zero) throw Win32("Dependencia ausente no controlador: " + dll);
                IntPtr local = ordinal.HasValue ? Native.GetProcAddressOrdinal(library, new IntPtr(ordinal.Value)) : Native.GetProcAddressName(library, symbol);
                if (local == IntPtr.Zero) throw Win32("Exportacao nao encontrada: " + key);
                IntPtr owner;
                if (!Native.GetModuleHandleExW(0x6, local, out owner)) throw Win32("Identificar modulo dono da exportacao");
                StringBuilder path = new StringBuilder(32768);
                uint length = Native.GetModuleFileNameW(owner, path, (uint)path.Capacity);
                if (length == 0) throw Win32("Caminho do modulo da exportacao");
                if (length >= path.Capacity) throw new IOException("Caminho do modulo truncado.");
                ModuleInfo match = null;
                foreach (ModuleInfo module in modules)
                    if (String.Equals(module.Path, path.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        if (match != null) throw new InvalidOperationException("Dependencia equivalente ambigua no alvo.");
                        match = module;
                    }
                if (match == null) throw new InvalidOperationException("Dependencia equivalente ausente no alvo: " + path);
                ulong localAddress = Address(local), ownerAddress = Address(owner);
                if (localAddress < ownerAddress || localAddress - ownerAddress + 16 > match.Size)
                    throw new InvalidOperationException("Endereco de exportacao invalido: " + key);
                ulong remoteAddress = match.Base + (localAddress - ownerAddress);
                byte[] remoteBytes = Read(remoteAddress, 16), localBytes = new byte[16];
                Marshal.Copy(local, localBytes, 0, localBytes.Length);
                for (int index = 0; index < 16; index++)
                    if (localBytes[index] != remoteBytes[index])
                        throw new InvalidOperationException("A dependencia difere entre controlador e alvo: " + key);
                resolved.Add(key, remoteAddress);
                return remoteAddress;
            }

            public ulong Call(ulong function, params ulong[] args)
            {
                if (InFlight) throw new InvalidOperationException("Chamada anterior ainda pendente.");
                if (args.Length > 4) throw new ArgumentException("Maximo de quatro argumentos.");
                if (code == 0)
                {
                    code = Allocate((uint)Thunk.Length, 0);
                    Write(code, Thunk);
                    Protect(code, (uint)Thunk.Length, 0x20);
                    Flush(code, (uint)Thunk.Length);
                }
                if (parameters == 0) parameters = Allocate(56, 0);
                byte[] data = new byte[56];
                Write64(data, 0, function);
                for (int index = 0; index < args.Length; index++) Write64(data, (ulong)(8 + index * 8), args[index]);
                Write(parameters, data);
                uint threadId;
                IntPtr thread = Native.CreateRemoteThread(handle, IntPtr.Zero, UIntPtr.Zero, Pointer(code), Pointer(parameters), 0, out threadId);
                if (thread == IntPtr.Zero) throw Win32("CreateRemoteThread");
                InFlight = true;
                try
                {
                    uint wait = Native.WaitForSingleObject(thread, 15000);
                    if (wait == 0xffffffff) throw Win32("Aguardar thread; memoria preservada");
                    if (wait != 0) throw new TimeoutException("Chamada nao terminou (espera " + wait + "); memoria preservada.");
                    InFlight = false;
                    uint exitCode;
                    if (!Native.GetExitCodeThread(thread, out exitCode)) throw Win32("Resultado da thread");
                    byte[] response = Read(parameters, 56);
                    if (exitCode != 0 || U64(response, 48) != 1)
                        throw new InvalidOperationException("Chamada remota falhou: exit=0x" + exitCode.ToString("X") + ".");
                    return U64(response, 40);
                }
                finally { Native.CloseHandle(thread); }
            }

            public string TryFree(ulong address)
            {
                if (Native.VirtualFreeEx(handle, Pointer(address), UIntPtr.Zero, 0x8000)) return null;
                return Win32("VirtualFreeEx em 0x" + address.ToString("X")).Message;
            }

            public void Close(StringBuilder report)
            {
                if (handle == IntPtr.Zero) return;
                if (!InFlight)
                {
                    foreach (ulong address in new ulong[] { parameters, code })
                    {
                        if (address == 0) continue;
                        string error = TryFree(address);
                        if (error != null) report.AppendLine(error);
                    }
                }
                else report.AppendLine("Thread ainda pendente: parametros e thunk preservados.");
                if (!Native.CloseHandle(handle)) report.AppendLine(Win32("Fechar handle do processo").Message);
                handle = IntPtr.Zero;
            }
        }

        private static class Native
        {
            [StructLayout(LayoutKind.Sequential)]
            internal struct FileTime { public uint Low, High; }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            internal struct ModuleEntry
            {
                public uint Size, ModuleId, ProcessId, GlobalUsage, ProcessUsage;
                public IntPtr Base;
                public uint BaseSize;
                public IntPtr Module;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Path;
            }

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(IntPtr handle);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetProcessTimes(IntPtr process, out FileTime creation, out FileTime exit, out FileTime kernel, out FileTime user);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref uint size);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWow64Process2(IntPtr process, out ushort machine, out ushort nativeMachine);
            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
            internal static extern IntPtr OpenMutexW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool Module32FirstW(IntPtr snapshot, ref ModuleEntry entry);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool Module32NextW(IntPtr snapshot, ref ModuleEntry entry);
            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint allocation, uint protection);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint freeType);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool VirtualProtectEx(IntPtr process, IntPtr address, UIntPtr size, uint protection, out uint previous);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr size, out UIntPtr written);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ReadProcessMemory(IntPtr process, IntPtr address, [Out] byte[] bytes, UIntPtr size, out UIntPtr read);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);
            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, UIntPtr stackSize, IntPtr start, IntPtr parameter, uint flags, out uint threadId);
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetExitCodeThread(IntPtr thread, out uint code);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
            internal static extern IntPtr GetModuleHandleW(string name);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetModuleHandleExW(uint flags, IntPtr address, out IntPtr module);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
            internal static extern uint GetModuleFileNameW(IntPtr module, StringBuilder path, uint size);
            [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
            internal static extern IntPtr GetProcAddressName(IntPtr module, string name);
            [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", ExactSpelling = true, SetLastError = true)]
            internal static extern IntPtr GetProcAddressOrdinal(IntPtr module, IntPtr ordinal);
        }
    }
}
