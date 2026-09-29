using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace LotroVisual
{
    internal sealed class Client
    {
        public int Pid;
        public string Exe;
        public long Started;
        public bool HasWindow;
        public string Key { get { return Pid + ":" + Started; } }
        public override string ToString() { return "LOTRO 64 bits  •  PID " + Pid; }
        public static List<Client> Find()
        {
            var found = new List<Client>();
            foreach (var p in Process.GetProcessesByName("lotroclient64"))
            {
                using (p)
                {
                    try
                    {
                        found.Add(new Client { Pid = p.Id, Exe = p.MainModule.FileName,
                            Started = p.StartTime.ToUniversalTime().ToFileTimeUtc(), HasWindow = p.MainWindowHandle != IntPtr.Zero });
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception e)
                    {
                        throw new InvalidOperationException("O LOTRO foi encontrado, mas o Windows recusou consultar o processo. " + e.Message, e);
                    }
                }
            }
            return found.OrderBy(x => x.Pid).ToList();
        }
        public void Verify()
        {
            using (var p = Process.GetProcessById(Pid))
            {
                if (p.HasExited || !String.Equals(p.ProcessName, "lotroclient64", StringComparison.OrdinalIgnoreCase) ||
                    p.StartTime.ToUniversalTime().ToFileTimeUtc() != Started)
                    throw new InvalidOperationException("Esta sessão do jogo terminou. Atualize a lista e carregue na nova sessão.");
            }
        }
    }

    internal sealed class OverlayInfo
    {
        public bool Ready, Legacy, Enabled;
        public long PublishedUtc;
        public Dictionary<string, object> Data;
        public string Text(string key) { return Data != null && Data.ContainsKey(key) ? Convert.ToString(Data[key]) : ""; }
    }

    internal static class Backend
    {
        public static readonly string LogDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LotroVisual", "registros");
        public static readonly string SettingsPath = Path.Combine(Path.GetDirectoryName(LogDir), "preferencias.json");
        public static string StatusPath(int pid) { return Path.Combine(Path.GetTempPath(), "LotroVisualInterno", "pid-" + pid, "estado.json"); }
        public static string EventName(string suffix, int pid) { return "Local\\CodexLotroVisual" + suffix + "-" + pid; }

        public static bool EventExists(string suffix, int pid, out bool signaled)
        {
            signaled = false;
            EventWaitHandle handle;
            if (!EventWaitHandle.TryOpenExisting(EventName(suffix, pid), System.Security.AccessControl.EventWaitHandleRights.Synchronize, out handle)) return false;
            using (handle) signaled = handle.WaitOne(0);
            return true;
        }
        public static OverlayInfo Read(Client client)
        {
            var info = new OverlayInfo();
            bool signaled;
            info.Ready = EventExists("Ready", client.Pid, out signaled) && signaled;
            info.Legacy = !info.Ready && EventExists("Stop", client.Pid, out signaled) && !EventExists("Enabled", client.Pid, out signaled);
            info.Enabled = EventExists("Enabled", client.Pid, out signaled) && signaled;
            var path = StatusPath(client.Pid);
            try
            {
                if (File.Exists(path) && File.GetLastWriteTimeUtc(path).ToFileTimeUtc() >= client.Started)
                {
                    var published = File.GetLastWriteTimeUtc(path).Ticks;
                    Dictionary<string, object> data;
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                    if (data != null && data.ContainsKey("inicio_processo") && Convert.ToString(data["inicio_processo"]) == client.Started.ToString())
                    { info.Data = data; info.PublishedUtc = published; }
                }
            }
            catch (IOException) { }
            catch (ArgumentException) { }
            return info;
        }
        public static byte[] Payload()
        {
            var assembly = Assembly.GetExecutingAssembly();
            byte[] data;
            using (var input = assembly.GetManifestResourceStream("visual-lotro-x64.dll"))
            {
                if (input == null) throw new InvalidOperationException("A DLL não foi incorporada ao executável.");
                using (var output = new MemoryStream()) { input.CopyTo(output); data = output.ToArray(); }
            }
            string expected;
            using (var input = assembly.GetManifestResourceStream("payload-manifest.json"))
            using (var reader = new StreamReader(input, Encoding.UTF8))
                expected = Convert.ToString(new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd())["sha256"]);
            string actual;
            using (var sha = SHA256.Create()) actual = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
            if (actual != expected) throw new InvalidOperationException("A DLL incorporada não corresponde ao manifesto de compilação.");
            NativeLoader.Validate(data);
            return data;
        }
        public static bool IsCurrent(OverlayInfo info)
        { return info.Ready && info.Text("protocolo") == "3" && info.Text("revisao_visual") == "4"; }
        public static string Load(Client client, bool enabled)
        {
            client.Verify();
            var info = Read(client);
            if (info.Legacy) throw new InvalidOperationException("Há uma versão antiga do visual nesta sessão. Feche e reabra o jogo uma vez para usar os novos controles.");
            if (IsCurrent(info)) { SetEnabled(client, enabled); return "DLL já carregada nesta sessão; controle atualizado."; }
            if (info.Ready && !IsCurrent(info))
            {
                Stop(client);
                // A DLL antiga encerra janela/worker antes de iniciar a versão nova.
                Thread.Sleep(100);
                info = Read(client);
            }
            long previousPublication = info.PublishedUtc;
            var report = NativeLoader.Load(client.Pid, client.Exe, client.Started, Payload());
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 12000)
            {
                client.Verify();
                info = Read(client);
                if (IsCurrent(info) && info.Data != null &&
                    (info.Text("estado") == "ativo" || info.Text("estado") == "pausado"))
                {
                    SetEnabled(client, enabled);
                    return report + Environment.NewLine + "Inicialização da DLL confirmada; círculos " + (enabled ? "ligados." : "desligados.");
                }
                var state = info.Text("estado");
                if (info.PublishedUtc > previousPublication &&
                    (state.StartsWith("falha", StringComparison.Ordinal) || state == "versao_nao_suportada" || state == "janela_nao_encontrada" || state == "encerrado"))
                    throw new InvalidOperationException("A DLL foi mapeada, mas o visual não iniciou: " + state + ". Erro Windows: " + info.Text("erro_windows") + ". " + report);
                Thread.Sleep(100);
            }
            throw new InvalidOperationException("O carregador terminou, mas a DLL não confirmou que o visual está pronto em 12 segundos. Não houve nova tentativa automática. " + report);
        }
        public static void SetEnabled(Client client, bool enabled)
        {
            client.Verify();
            bool ready;
            if (!EventExists("Ready", client.Pid, out ready) || !ready) throw new InvalidOperationException("Carregue a DLL antes de controlar os círculos.");
            using (var handle = EventWaitHandle.OpenExisting(EventName("Enabled", client.Pid), System.Security.AccessControl.EventWaitHandleRights.Modify))
            {
                if (enabled) handle.Set(); else handle.Reset();
            }
        }
        public static void Stop(Client client)
        {
            client.Verify();
            using (var handle = EventWaitHandle.OpenExisting(EventName("Stop", client.Pid), System.Security.AccessControl.EventWaitHandleRights.Modify)) handle.Set();
            var timer = Stopwatch.StartNew();
            bool signaled;
            while (timer.ElapsedMilliseconds < 4000 && EventExists("Stop", client.Pid, out signaled)) Thread.Sleep(50);
            if (EventExists("Stop", client.Pid, out signaled)) throw new InvalidOperationException("Parada solicitada, mas o encerramento ainda não foi confirmado.");
        }
        public static string Log(string operation, Client client, string text, bool ok)
        {
            Directory.CreateDirectory(LogDir);
            var path = Path.Combine(LogDir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + operation + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".json");
            var data = new { quando = DateTime.Now.ToString("o"), operacao = operation, sucesso = ok,
                pid = client == null ? 0 : client.Pid, inicio_processo = client == null ? "" : client.Started.ToString(), resultado = text };
            File.WriteAllText(path, new JavaScriptSerializer().Serialize(data), new UTF8Encoding(false));
            return path;
        }
    }

    // A tentativa automática é vinculada ao PID E à criação do processo.
    // Uma falha só pode ser repetida por clique explícito; o timer não reinjeta.
    internal sealed class Lifecycle
    {
        public bool Armed, Reload = true, DesiredEnabled = true;
        private readonly HashSet<string> attempted = new HashSet<string>();
        public bool ShouldLoad(Client client, bool busy, bool ready)
        { return Armed && Reload && !busy && client != null && client.HasWindow && !ready && !attempted.Contains(client.Key); }
        public void Mark(Client client) { attempted.Add(client.Key); }
        public void Clear() { Armed = false; }
        public static int ChooseIndex(List<Client> now, string previous, string[] previousKeys, bool explicitSelection)
        {
            if (now.Count > 1 && !explicitSelection) return -1;
            int index = now.FindIndex(c => c.Key == previous);
            if (index >= 0) return index;
            if (now.Count == 1 && !(previousKeys.Length > 1 && previousKeys.Contains(now[0].Key))) return 0;
            return -1;
        }
    }

    internal sealed class Panel : Form
    {
        private readonly ComboBox clients = new ComboBox();
        private readonly Label processLabel = new Label(), visualLabel = new Label(), countsLabel = new Label();
        private readonly Button load = new Button(), on = new Button(), off = new Button(), stop = new Button();
        private readonly CheckBox reload = new CheckBox();
        private readonly TextBox history = new TextBox();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly Lifecycle lifecycle = new Lifecycle();
        private bool busy, refreshing, selectionExplicit;
        private string lastList = "", selectedKey = "", lastRefreshError = "";

        public Panel()
        {
            Text = "LOTRO • Círculos verdes";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(710, 590);
            MinimumSize = new Size(710, 620);
            Font = new Font("Segoe UI", 10);
            BackColor = Color.FromArgb(245, 247, 249);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            var title = new Label { Text = "Círculos verdes", AutoSize = true, Font = new Font("Segoe UI", 22, FontStyle.Bold), Location = new Point(24, 20) };
            Controls.Add(title);
            var subtitle = new Label { Text = "Carregue o visual e controle os círculos nesta janela.", AutoSize = true, Location = new Point(27, 70) };
            Controls.Add(subtitle);
            clients.Location = new Point(28, 106); clients.Size = new Size(652, 30); clients.DropDownStyle = ComboBoxStyle.DropDownList;
            clients.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            clients.SelectedIndexChanged += delegate { if (!refreshing) { selectionExplicit = true; var c = clients.SelectedItem as Client; selectedKey = c == null ? "" : c.Key; RefreshState(); } };
            Controls.Add(clients);
            LabelAt(processLabel, 151, "Procurando o jogo…");
            LabelAt(visualLabel, 180, "DLL ainda não carregada.");
            LabelAt(countsLabel, 209, "");
            AddButton(load, "Carregar DLL", 28, 253, 200, async delegate { await LoadSelected(false); });
            AddButton(on, "Ligar círculos", 244, 253, 210, delegate { Toggle(true); });
            AddButton(off, "Desligar círculos", 470, 253, 210, delegate { Toggle(false); });
            load.BackColor = Color.FromArgb(28, 104, 58); load.ForeColor = Color.White;
            reload.Text = "Recarregar ao reabrir o jogo (com este painel aberto)";
            reload.Location = new Point(28, 315); reload.Size = new Size(652, 28); reload.Checked = true;
            try
            {
                if (File.Exists(Backend.SettingsPath))
                {
                    var settings = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Backend.SettingsPath));
                    if (settings.ContainsKey("recarregar")) reload.Checked = Convert.ToBoolean(settings["recarregar"]);
                }
            }
            catch (Exception) { }
            lifecycle.Reload = reload.Checked;
            reload.CheckedChanged += delegate
            {
                lifecycle.Reload = reload.Checked;
                try { Directory.CreateDirectory(Path.GetDirectoryName(Backend.SettingsPath)); File.WriteAllText(Backend.SettingsPath, new JavaScriptSerializer().Serialize(new { recarregar = reload.Checked })); }
                catch (Exception e) { Append("Preferência não salva: " + e.Message); }
            };
            Controls.Add(reload);
            var hint = new Label { Text = "Após o primeiro carregamento, novas sessões recebem o último estado dos círculos.", Location = new Point(30, 347), Size = new Size(650, 36), ForeColor = Color.DimGray };
            Controls.Add(hint);
            history.Location = new Point(28, 391); history.Size = new Size(652, 120); history.Multiline = true;
            history.ReadOnly = true; history.ScrollBars = ScrollBars.Vertical; history.BackColor = Color.White;
            history.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(history);
            AddButton(stop, "Encerrar visual", 28, 532, 190, async delegate { await StopSelected(); });
            stop.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            var logs = new Button(); AddButton(logs, "Abrir registros", 490, 532, 190, delegate { try { Directory.CreateDirectory(Backend.LogDir); Process.Start("explorer.exe", Backend.LogDir); } catch (Exception e) { Append(e.Message); } });
            logs.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            timer.Interval = 1000;
            timer.Tick += async delegate
            {
                RefreshState();
                var c = Current();
                if (clients.Items.Count > 1 && !selectionExplicit) return;
                try { if (lifecycle.ShouldLoad(c, busy, c != null && Backend.Read(c).Ready)) await LoadSelected(true); }
                catch (Exception e) { if (lastRefreshError != e.Message) { Append(e.Message); lastRefreshError = e.Message; } }
            };
            Shown += delegate
            {
                Append("Pronto. Clique em Carregar DLL para iniciar."); RefreshState();
                try
                {
                    var c = Current(); var info = c == null ? null : Backend.Read(c);
                    if (info != null && info.Ready) { lifecycle.Armed = true; lifecycle.DesiredEnabled = info.Enabled; lifecycle.Mark(c); Append("Visual existente reconhecido. Acompanhamento das reaberturas ativo conforme a opção acima."); }
                }
                catch (Exception e) { Append(e.Message); }
                timer.Start();
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (busy) { e.Cancel = true; Append("Aguarde a operação em andamento antes de fechar o painel."); }
                else timer.Stop();
            };
        }
        private void LabelAt(Label label, int y, string text)
        { label.Text = text; label.Location = new Point(28, y); label.Size = new Size(652, 27); label.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top; Controls.Add(label); }
        private void AddButton(Button button, string text, int x, int y, int width, EventHandler click)
        { button.Text = text; button.Location = new Point(x, y); button.Size = new Size(width, 45); button.FlatStyle = FlatStyle.Flat; button.Click += click; Controls.Add(button); }
        private Client Current() { return clients.SelectedItem as Client; }
        private void Append(string text)
        { history.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + Environment.NewLine); }
        private void RefreshState()
        {
            if (IsDisposed || refreshing) return;
            refreshing = true;
            try
            {
                var list = Client.Find();
                string signature = String.Join("|", list.Select(x => x.Key));
                if (signature != lastList)
                {
                    string previous = selectedKey;
                    var previousKeys = lastList.Split('|');
                    clients.Items.Clear(); foreach (var item in list) clients.Items.Add(item);
                    clients.SelectedIndex = Lifecycle.ChooseIndex(list, previous, previousKeys, selectionExplicit);
                    if (Current() == null || Current().Key != previous) selectionExplicit = false;
                    selectedKey = Current() == null ? "" : Current().Key;
                    if (list.Count == 0 && lastList != "") Append("O jogo fechou. Aguardando uma nova sessão.");
                    if (selectedKey != "" && selectedKey != previous) Append("Sessão detectada: PID " + Current().Pid + ".");
                    lastList = signature;
                }
                // Atualiza disponibilidade da janela mesmo quando o PID não mudou.
                var current = Current();
                if (current != null) { var fresh = list.FirstOrDefault(x => x.Key == current.Key); if (fresh != null) current.HasWindow = fresh.HasWindow; }
                if (list.Count > 1 && !selectionExplicit) current = null;
                clients.Enabled = !busy;
                var info = current == null ? new OverlayInfo() : Backend.Read(current);
                processLabel.Text = current == null ? (list.Count == 0 ? "Jogo fechado • aguardando LOTRO" : "Escolha qual janela do LOTRO usar acima.") : "Jogo aberto • PID " + current.Pid + (current.HasWindow ? "" : " • aguardando a janela");
                visualLabel.Text = busy ? "Operação em andamento…" : info.Legacy ? "Versão antiga ativa • reabra o jogo para usar este painel." : info.Ready ? (info.Enabled ? "Círculos ligados" : "Círculos desligados • DLL pronta para religar") : "DLL ainda não carregada nesta sessão.";
                if (!busy && !info.Ready && info.Text("estado") != "" && info.Text("estado") != "encerrado") visualLabel.Text += " Estado: " + info.Text("estado");
                countsLabel.Text = info.Ready ? "Entidades: " + info.Text("entidades") + "   •   Círculos calculados: " + info.Text("circulos") + (info.Text("visivel").Equals("True", StringComparison.OrdinalIgnoreCase) ? "   •   Visual em exibição" : "   •   Janela do visual oculta") : "";
                load.Enabled = !busy && current != null && current.HasWindow && !info.Ready && !info.Legacy;
                on.Enabled = !busy && info.Ready && !info.Enabled;
                off.Enabled = !busy && info.Ready && info.Enabled;
                stop.Enabled = !busy && (info.Ready || info.Legacy);
                lastRefreshError = "";
            }
            catch (Exception e)
            {
                load.Enabled = on.Enabled = off.Enabled = stop.Enabled = false;
                processLabel.Text = "Não foi possível consultar o jogo.";
                if (lastRefreshError != e.Message) { Append(e.Message); lastRefreshError = e.Message; }
            }
            finally { refreshing = false; }
        }
        private async Task LoadSelected(bool automatic)
        {
            var client = Current();
            if (client == null || busy) return;
            lifecycle.Armed = true; lifecycle.Mark(client); busy = true; RefreshState();
            Append((automatic ? "Recarregando na nova sessão" : "Carregando a DLL") + " • PID " + client.Pid + "…");
            try
            {
                var message = await Task.Run(() => Backend.Load(client, lifecycle.DesiredEnabled));
                Append("DLL pronta. Círculos " + (lifecycle.DesiredEnabled ? "ligados." : "desligados."));
                try { Backend.Log("carregar", client, message, true); }
                catch (Exception e) { Append("Visual pronto, mas não foi possível salvar o registro: " + e.Message); }
            }
            catch (Exception e) { Failure("carregar", client, e); }
            finally { busy = false; RefreshState(); }
        }
        private void Toggle(bool enabled)
        {
            var client = Current(); if (client == null || busy) return;
            try { Backend.SetEnabled(client, enabled); lifecycle.DesiredEnabled = enabled; lifecycle.Armed = true; lifecycle.Mark(client); Append(enabled ? "Círculos ligados." : "Círculos desligados."); }
            catch (Exception e) { Failure("controle", client, e); }
            RefreshState();
        }
        private async Task StopSelected()
        {
            var client = Current(); if (client == null || busy) return;
            lifecycle.Clear(); busy = true; RefreshState();
            try { await Task.Run(() => Backend.Stop(client)); Append("Visual encerrado. Clique em Carregar DLL para voltar a usar."); }
            catch (Exception e) { Failure("parar", client, e); }
            finally { busy = false; RefreshState(); }
        }
        private void Failure(string operation, Client client, Exception e)
        {
            Append("Falha: " + e.Message);
            try { Append("Detalhes: " + Backend.Log(operation, client, e.ToString(), false)); }
            catch (Exception logError) { Append("Não foi possível salvar o registro: " + logError.Message); }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--simular")
            {
                GameMenu.SimulacaoAtiva = true;
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new GameMenu());
                return 0;
            }
            if (args.Length > 0) return Command(args);
            bool created;
            using (var mutex = new Mutex(false, "Local\\CodexLotroVisualPanel-v3", out created))
            {
                if (!created) { MessageBox.Show("O painel já está aberto.", "Círculos verdes"); return 0; }
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new GameMenu());
            }
            return 0;
        }
        private static int Command(string[] args)
        {
            string destination = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(Backend.LogDir, "diagnostico.json");
            try
            {
                object result;
                var command = args[0];
                if (command == "--autoteste") result = SelfTest();
                else if (command == "--teste-menu") result = GameMenu.TestFilters();
                else if (command == "--diagnostico")
                {
                    var bytes = Backend.Payload();
                    result = new { ok = true, payload_bytes = bytes.Length, processos = Client.Find().Select(c => new { pid = c.Pid, exe = c.Exe, inicio = c.Started.ToString(), janela = c.HasWindow, pronto = Backend.Read(c).Ready }).ToArray() };
                }
                else
                {
                    var clients = Client.Find();
                    if (args.Length > 2) { int pid = Int32.Parse(args[2]); clients = clients.Where(c => c.Pid == pid).ToList(); }
                    if (clients.Count != 1) throw new InvalidOperationException("A ação exige exatamente um LOTRO aberto.");
                    var client = clients[0]; string message;
                    if (command == "--carregar") message = Backend.Load(client, true);
                    else if (command == "--ligar" || command == "--desligar") { Backend.SetEnabled(client, command == "--ligar"); message = "Controle enviado."; }
                    else if (command == "--parar") { Backend.Stop(client); message = "Visual encerrado."; }
                    else throw new ArgumentException("Opção desconhecida: " + command);
                    result = new { ok = true, pid = client.Pid, mensagem = message };
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.WriteAllText(destination, new JavaScriptSerializer().Serialize(result), new UTF8Encoding(false));
                return 0;
            }
            catch (Exception e)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.WriteAllText(destination, new JavaScriptSerializer().Serialize(new { ok = false, erro = e.ToString() }), new UTF8Encoding(false));
                return 1;
            }
        }
        private static object SelfTest()
        {
            Backend.Payload();
            var old = new Client { Pid = 100, Started = 1, HasWindow = true };
            var reused = new Client { Pid = 100, Started = 2, HasWindow = true };
            var next = new Client { Pid = 200, Started = 3, HasWindow = true };
            var life = new Lifecycle();
            Require(!life.ShouldLoad(old, false, false), "Não inicia sem carregamento solicitado");
            life.Armed = true;
            Require(life.ShouldLoad(old, false, false), "Primeira sessão");
            life.Mark(old);
            Require(!life.ShouldLoad(old, false, false), "Não repete automaticamente carga com falha");
            Require(life.ShouldLoad(reused, false, false), "PID reutilizado reconhecido como nova sessão");
            Require(life.ShouldLoad(next, false, false), "Novo PID reconhecido");
            Require(!life.ShouldLoad(next, true, false), "Não sobrepõe cargas");
            Require(!life.ShouldLoad(next, false, true), "Não carrega DLL duplicada");
            Require(!life.ShouldLoad(null, false, false), "Jogo fechado");
            next.HasWindow = false;
            Require(!life.ShouldLoad(next, false, false), "Aguarda janela do jogo");
            life.Clear(); next.HasWindow = true;
            Require(!life.ShouldLoad(next, false, false), "Encerrar visual cancela recarga");
            Require(Lifecycle.ChooseIndex(new List<Client> { next }, old.Key, new[] { old.Key, next.Key }, true) == -1, "Não troca automaticamente para cliente preexistente");
            Require(Lifecycle.ChooseIndex(new List<Client> { next }, old.Key, new[] { old.Key }, false) == 0, "Seleciona sessão recém-aberta");
            Require(Lifecycle.ChooseIndex(new List<Client> { old, next }, "", new string[0], false) == -1, "Exige seleção entre dois clientes");
            bool rejected = false; try { NativeLoader.Validate(new byte[64]); } catch (Exception) { rejected = true; }
            Require(rejected, "Rejeita DLL inválida");
            return new { ok = true, verificacoes = 15, descricao = "DLL incorporada, ciclo de processos, PID reutilizado, duplicatas, falhas, múltiplos clientes e espera da janela. Sem carregar no jogo." };
        }
        private static void Require(bool ok, string test) { if (!ok) throw new InvalidOperationException("Autoteste: " + test); }
    }
}
