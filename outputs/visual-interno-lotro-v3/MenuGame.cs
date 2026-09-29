using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace LotroVisual
{
    internal sealed class EntityRow
    {
        public string Id, Name, Type, NameKey;
        public double Distance;
        public bool OnScreen, Selected;
        public bool? Selectable, Attackable;
        public static List<EntityRow> Read(OverlayInfo info)
        {
            var result = new List<EntityRow>();
            if (info.Data == null || !info.Data.ContainsKey("lista")) return result;
            foreach (var item in (IEnumerable)info.Data["lista"])
            {
                var row = item as Dictionary<string, object>; if (row == null) continue;
                result.Add(new EntityRow { Id = Convert.ToString(row["id"]), Name = Convert.ToString(row["nome"]),
                    NameKey = row.ContainsKey("chave_nome") ? Convert.ToString(row["chave_nome"]) : "0x0",
                    Type = Convert.ToString(row["tipo"]), Distance = Convert.ToDouble(row["distancia"]),
                    OnScreen = Convert.ToBoolean(row["na_tela"]), Selected = Convert.ToBoolean(row["selecionado"]),
                    Selectable = row["selecionavel"] == null ? (bool?)null : Convert.ToBoolean(row["selecionavel"]),
                    Attackable = row["atacavel"] == null ? (bool?)null : Convert.ToBoolean(row["atacavel"]) });
            }
            return result.OrderBy(x => x.Distance).ToList();
        }
    }
    internal sealed class UserTag
    {
        public string Nome { get; set; }
        public string Categoria { get; set; }
        public bool Atacavel { get; set; }
    }
    internal sealed class RadarPreferences
    {
        public int Estilo { get; set; }
        public int Tamanho { get; set; }
        public int Alcance { get; set; }
        public bool Recarregar { get; set; }
        public bool Nomes { get; set; }
        public string Busca { get; set; }
        public string Filtro { get; set; }
        // Legacy data preserved on disk, never consulted for names or categories.
        public Dictionary<string, UserTag> Identificacoes { get; set; }
        public RadarPreferences()
        { Tamanho = 100; Alcance = 80; Recarregar = true; Filtro = "Todos"; Busca = ""; Identificacoes = new Dictionary<string, UserTag>(); }
    }
    internal static class FilterLogic
    {
        public static string Name(EntityRow row)
        { return !String.IsNullOrWhiteSpace(row.Name) ? row.Name : row.Id; }
        public static string Category(EntityRow row)
        {
            if (row.Type == "monstro") return "Mobs/monstros";
            if (row.Type == "jogador") return "Jogador";
            if (row.Type == "npc") return "NPC";
            if (row.Type == "realnpc") return "RealNPC";
            if (row.Type == "objeto") return "Objeto";
            return "Não classificado";
        }
        public static bool Match(EntityRow row, string category, string search)
        {
            bool match = category == "Todos" || category == Category(row);
            return match && (String.IsNullOrWhiteSpace(search) || Name(row).IndexOf(search.Trim(), StringComparison.OrdinalIgnoreCase) >= 0 || row.Id.IndexOf(search.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
    internal static class RadarOptions
    {
        public static byte[] Encode(Client client, uint revision, int style, int size, int range, bool labels,
                                    bool restricted, IntPtr menu, ulong focus, IList<ulong> ids, IList<string> names = null)
        {
            if (ids.Count > 512 || style < 0 || style > 2 || size < 20 || size > 220 || range < 10 || range > 120) throw new ArgumentException("Opções fora dos limites.");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0x33564c43u); writer.Write(3u); writer.Write((ulong)client.Started); writer.Write(revision);
                writer.Write((uint)style); writer.Write((uint)size); writer.Write((float)range); writer.Write(labels ? 1u : 0u);
                writer.Write(restricted ? 1u : 0u); writer.Write((ulong)menu.ToInt64()); writer.Write(focus); writer.Write((uint)ids.Count);
                for (int i = 0; i < 512; i++) writer.Write(i < ids.Count ? ids[i] : 0UL);
                writer.Write(new byte[512 * 96 * 2]); // Reserved legacy field; names come from the DLL.
                return stream.ToArray();
            }
        }
        public static void Write(Client client, byte[] bytes)
        {
            client.Verify();
            string directory = Path.GetDirectoryName(Backend.StatusPath(client.Pid));
            Directory.CreateDirectory(directory);
            string target = Path.Combine(directory, "opcoes.bin"), tmp = Path.Combine(directory, "opcoes-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllBytes(tmp, bytes);
            if (File.Exists(target)) File.Replace(tmp, target, null); else File.Move(tmp, target);
        }
    }
    internal static class MenuNative
    {
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out RectangleNative rect);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool ReleaseCapture();
        [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
        [StructLayout(LayoutKind.Sequential)] internal struct RectangleNative { public int Left, Top, Right, Bottom; }
    }

    internal sealed class GameMenu : Form
    {
        private readonly ComboBox clients = new ComboBox(), category = new ComboBox(), style = new ComboBox();
        private readonly TextBox search = new TextBox();
        private readonly CheckBox reload = new CheckBox(), onlyChosen = new CheckBox(), labels = new CheckBox();
        private readonly NumericUpDown range = new NumericUpDown(), markerSize = new NumericUpDown();
        private readonly ListView list = new ListView();
        private readonly Label status = new Label(), help = new Label(), total = new Label();
        private readonly Button load = new Button(), toggle = new Button();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly Lifecycle lifecycle = new Lifecycle();
        private readonly HashSet<string> chosen = new HashSet<string>();
        private RadarPreferences preferences;
        private List<EntityRow> rows = new List<EntityRow>();
        private string lastList = "", selectedKey = "", lastError = "", optionSignature = "", rowsSignature = "";
        private bool busy, refreshing, filling, explicitSelection, closing, ready, enabled, first = true, hotkey;
        private uint revision;
        private IntPtr gameWindow;
        private DateTime lastErrorTime;
        public static bool SimulacaoAtiva = false;
        private static string SimulacaoPath { get { return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath), "simulacao.json"); } }
        private const string PreferencesFileName = "radar-v3.json";
        private static readonly Color Background = Color.FromArgb(20, 27, 28), Surface = Color.FromArgb(32, 42, 43), Ink = Color.FromArgb(227, 237, 230), Accent = Color.FromArgb(124, 215, 174);
        private string PreferencesFile { get { return Path.Combine(Path.GetDirectoryName(Backend.LogDir), PreferencesFileName); } }

        public GameMenu()
        {
            try { preferences = File.Exists(PreferencesFile) ? new JavaScriptSerializer().Deserialize<RadarPreferences>(File.ReadAllText(PreferencesFile)) : new RadarPreferences(); }
            catch (Exception) { preferences = new RadarPreferences(); }
            if (preferences == null) preferences = new RadarPreferences();
            if (preferences.Identificacoes == null) preferences.Identificacoes = new Dictionary<string, UserTag>();
            Text = "LOTRO • Entidades próximas"; FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(700, 730); MinimumSize = new Size(700, 730);
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9.5f); BackColor = Background; ForeColor = Ink; DoubleBuffered = true; KeyPreview = true;
            TopMost = true;
            var heading = Label("ENTIDADES PRÓXIMAS", 22, 16, 500, 27); heading.Font = new Font("Segoe UI Semibold", 15);
            heading.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { MenuNative.ReleaseCapture(); MenuNative.SendMessage(Handle, 0xa1, new IntPtr(2), IntPtr.Zero); } };
            MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { MenuNative.ReleaseCapture(); MenuNative.SendMessage(Handle, 0xa1, new IntPtr(2), IntPtr.Zero); } };
            var close = Button("F6  •  Ocultar", 554, 14, 124, 31); close.Click += delegate { HideMenu(true); };
            Label("Marcadores discretos · filtros · lista ao vivo", 23, 46, 620, 22).ForeColor = Color.FromArgb(152, 175, 165);
            Place(clients, 22, 79, 332, 30); clients.DropDownStyle = ComboBoxStyle.DropDownList;
            clients.SelectedIndexChanged += delegate { if (!filling) { explicitSelection = true; Client c = Current(); selectedKey = c == null ? "" : c.Key; chosen.Clear(); optionSignature = ""; RefreshNow(); } };
            ConfigureButton(load, "Carregar", 366, 77, 143, 34); load.Click += async delegate { await LoadCurrent(); };
            ConfigureButton(toggle, "Ligar visual", 520, 77, 158, 34); toggle.Click += delegate { ToggleVisual(); };
            status.SetBounds(22, 118, 654, 22); status.ForeColor = Accent; Controls.Add(status);
            Place(category, 22, 165, 194, 30); category.DropDownStyle = ComboBoxStyle.DropDownList;
            category.Items.AddRange(new object[] { "Todos", "Mobs/monstros", "NPC", "RealNPC", "Objeto", "Jogador", "Não classificado" });
            if (preferences.Filtro == "Atacáveis" || preferences.Filtro == "Mob (Atacável)" || preferences.Filtro == "Mob" || preferences.Filtro == "Mobs") preferences.Filtro = "Mobs/monstros";
            category.SelectedItem = preferences.Filtro; if (category.SelectedIndex < 0) category.SelectedIndex = 0;
            Place(search, 229, 166, 447, 27); search.Text = preferences.Busca ?? "";
            Label("Tipo", 23, 145, 170, 20); Label("Buscar por nome ou ID", 230, 145, 400, 20);
            list.SetBounds(22, 207, 654, 300); list.View = View.Details; list.FullRowSelect = true; list.CheckBoxes = true;
            list.MultiSelect = false; list.HideSelection = false; list.BackColor = Surface; list.ForeColor = Ink; list.BorderStyle = BorderStyle.None;
            list.Columns.Add("Nome / ID", 282); list.Columns.Add("Categoria", 174); list.Columns.Add("Dist.", 70); list.Columns.Add("Na tela", 72);
            Controls.Add(list);
            list.ItemChecked += delegate(object sender, ItemCheckedEventArgs e) { if (filling) return; string id = (string)e.Item.Tag; if (e.Item.Checked) chosen.Add(id); else chosen.Remove(id); UpdateOptions(); };
            total.SetBounds(22, 513, 330, 22); Controls.Add(total);
            Place(onlyChosen, 355, 509, 321, 27); onlyChosen.Text = "Mostrar apenas os itens marcados";
            Label("Estilo", 23, 553, 180, 20); Place(style, 22, 576, 193, 29); style.DropDownStyle = ComboBoxStyle.DropDownList;
            style.Items.AddRange(new object[] { "Cantos discretos", "Anel pequeno", "Indicador acima" }); style.SelectedIndex = Math.Max(0, Math.Min(2, preferences.Estilo));
            Label("Tamanho máx. (px)", 230, 553, 150, 20); Place(markerSize, 229, 576, 131, 29); markerSize.Minimum = 20; markerSize.Maximum = 220; markerSize.Increment = 10; markerSize.Value = Math.Max(20, Math.Min(220, preferences.Tamanho));
            Label("Alcance (unid.)", 378, 553, 150, 20); Place(range, 378, 576, 133, 29); range.Minimum = 10; range.Maximum = 120; range.Increment = 10; range.Value = Math.Max(10, Math.Min(120, preferences.Alcance));
            Place(labels, 525, 574, 151, 29); labels.Text = "Nomes no visual"; labels.Checked = preferences.Nomes;
            Place(reload, 22, 619, 426, 28); reload.Text = "Recarregar quando o jogo reabrir"; reload.Checked = preferences.Recarregar;
            var quit = Button("Sair do painel", 525, 617, 151, 31); quit.Click += delegate { closing = true; Close(); };
            help.SetBounds(22, 655, 654, 46); help.ForeColor = Color.FromArgb(152, 175, 165); Controls.Add(help);
            help.Text = "F6 abre e oculta o menu. Nomes e tipos automáticos das entidades carregadas próximas.\nMarcar itens escolhe quais destacar; não muda o alvo selecionado no jogo.";
            category.SelectedIndexChanged += delegate { Changed(true); }; search.TextChanged += delegate { Changed(true); };
            style.SelectedIndexChanged += delegate { Changed(false); }; markerSize.ValueChanged += delegate { Changed(false); };
            range.ValueChanged += delegate { Changed(false); }; labels.CheckedChanged += delegate { Changed(false); };
            onlyChosen.CheckedChanged += delegate { Changed(false); };
            reload.CheckedChanged += delegate { lifecycle.Reload = reload.Checked; SavePreferences(); };
            lifecycle.Reload = reload.Checked;
            KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) { HideMenu(true); e.Handled = true; } };
            timer.Interval = 500; timer.Tick += async delegate { RefreshNow(); Client c = Current(); if (lifecycle.ShouldLoad(c, busy, ready)) await LoadCurrent(); };
            tray.Icon = SystemIcons.Application; tray.Text = "LOTRO • F6 para abrir o menu"; tray.Visible = true;
            var trayMenu = new ContextMenuStrip(); trayMenu.Items.Add("Abrir menu", null, delegate { OpenMenu(); });
            trayMenu.Items.Add("Sair", null, delegate { closing = true; Close(); }); tray.ContextMenuStrip = trayMenu; tray.DoubleClick += delegate { OpenMenu(); };
            Shown += delegate { RefreshNow(); RegisterShortcut(); timer.Start(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (busy) { e.Cancel = true; Notice("Aguarde o carregamento terminar."); return; }
                if (!closing) { e.Cancel = true; HideMenu(true); return; }
                timer.Stop(); try { WriteOptions(IntPtr.Zero); } catch (Exception) { }
                MenuNative.UnregisterHotKey(Handle, 63); tray.Visible = false; tray.Dispose(); SavePreferences();
            };
        }
        private void Place(Control control, int x, int y, int w, int h)
        { control.SetBounds(x, y, w, h); control.BackColor = Surface; control.ForeColor = Ink; Controls.Add(control); }
        private Label Label(string text, int x, int y, int w, int h)
        { var label = new Label { Text = text, BackColor = Color.Transparent, ForeColor = Ink }; label.SetBounds(x, y, w, h); Controls.Add(label); return label; }
        private Button Button(string text, int x, int y, int w, int h)
        { var button = new Button(); ConfigureButton(button, text, x, y, w, h); return button; }
        private void ConfigureButton(Button button, string text, int x, int y, int w, int h)
        { Place(button, x, y, w, h); button.Text = text; button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderColor = Color.FromArgb(65, 91, 79); }
        private Client Current() { return clients.SelectedItem as Client; }
        private List<EntityRow> Filtered() { return rows.Where(r => FilterLogic.Match(r, Convert.ToString(category.SelectedItem), search.Text)).ToList(); }
        private void RegisterShortcut()
        {
            // Só reserva F6 quando o jogo/menu está em foco; outros programas continuam usando a tecla.
            uint pid; MenuNative.GetWindowThreadProcessId(MenuNative.GetForegroundWindow(), out pid);
            Client c = Current(); bool relevant = pid == (uint)Process.GetCurrentProcess().Id || c != null && pid == (uint)c.Pid;
            if (relevant && !hotkey)
            {
                hotkey = MenuNative.RegisterHotKey(Handle, 63, 0x4000, (uint)Keys.F6);
                if (!hotkey) Notice("F6 está ocupado por outro programa. Abra o menu pelo ícone perto do relógio.");
            }
            else if (!relevant && hotkey) { MenuNative.UnregisterHotKey(Handle, 63); hotkey = false; }
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x312 && m.WParam.ToInt32() == 63) { if (Visible) HideMenu(true); else OpenMenu(); return; }
            base.WndProc(ref m);
        }
        private void OpenMenu()
        {
            RefreshNow();
            if (gameWindow != IntPtr.Zero)
            {
                MenuNative.RectangleNative r;
                if (MenuNative.GetWindowRect(gameWindow, out r))
                {
                    Rectangle work = Screen.FromHandle(gameWindow).WorkingArea;
                    Location = new Point(Math.Max(work.Left, Math.Min(work.Right - Width, r.Left + 30)), Math.Max(work.Top, Math.Min(work.Bottom - Height, r.Top + 50)));
                }
            }
            Show(); Activate(); optionSignature = ""; UpdateOptions();
        }
        private void HideMenu(bool returnToGame)
        {
            Hide(); optionSignature = ""; UpdateOptions();
            if (returnToGame && gameWindow != IntPtr.Zero) MenuNative.SetForegroundWindow(gameWindow);
        }
        private void Notice(string message)
        { lastError = message; lastErrorTime = DateTime.UtcNow; status.Text = message; }
        private void RefreshNow()
        {
            if (refreshing || IsDisposed) return; refreshing = true;
            try
            {
                if (SimulacaoAtiva) { RefreshSimulacao(); return; }
                var found = Client.Find(); string signature = String.Join("|", found.Select(c => c.Key));
                if (signature != lastList)
                {
                    filling = true;
                    int index = Lifecycle.ChooseIndex(found, selectedKey, lastList.Split('|'), explicitSelection);
                    clients.Items.Clear(); foreach (Client c in found) clients.Items.Add(c); clients.SelectedIndex = index;
                    string next = Current() == null ? "" : Current().Key;
                    if (next != selectedKey) { chosen.Clear(); rows.Clear(); optionSignature = ""; explicitSelection = false; }
                    selectedKey = next; lastList = signature; filling = false;
                }
                Client current = Current();
                if (current != null) { Client fresh = found.FirstOrDefault(c => c.Key == current.Key); if (fresh != null) current.HasWindow = fresh.HasWindow; }
                OverlayInfo info = current == null ? new OverlayInfo() : Backend.Read(current);
                ready = Backend.IsCurrent(info); enabled = info.Enabled;
                if (info.Data != null && info.Data.ContainsKey("janela_jogo")) gameWindow = new IntPtr(Convert.ToInt64(info.Data["janela_jogo"]));
                else if (current != null) { using (var p = Process.GetProcessById(current.Pid)) gameWindow = p.MainWindowHandle; }
                else gameWindow = IntPtr.Zero;
                if (first && ready) { lifecycle.Armed = true; lifecycle.DesiredEnabled = enabled; lifecycle.Mark(current); }
                first = false;
                rows = ready ? EntityRow.Read(info) : new List<EntityRow>();
                FillList();
                load.Enabled = !busy && current != null && current.HasWindow && !ready;
                load.Text = info.Ready && !ready ? "Atualizar DLL" : "Carregar";
                toggle.Enabled = !busy && ready; toggle.Text = enabled && ready ? "Desligar visual" : "Ligar visual";
                clients.Enabled = !busy;
                if (DateTime.UtcNow - lastErrorTime > TimeSpan.FromSeconds(7))
                    status.Text = busy ? "Carregando…" : current == null ? "Aguardando o jogo. Se houver vários clientes, escolha um acima." :
                        !ready ? (info.Ready ? "Visual anterior ativo. Use Atualizar DLL para habilitar a nova versão." : "Jogo encontrado. Carregue o visual para iniciar.") :
                        (enabled ? "Visual ligado" : "Visual desligado") + "  ·  PID " + current.Pid + "  ·  F6 abre / oculta";
                RegisterShortcut();
                // O menu não acompanha a troca para outros aplicativos.
                uint foregroundPid; MenuNative.GetWindowThreadProcessId(MenuNative.GetForegroundWindow(), out foregroundPid);
                if (Visible && gameWindow != IntPtr.Zero && foregroundPid != (uint)Process.GetCurrentProcess().Id && (current == null || foregroundPid != (uint)current.Pid)) HideMenu(false);
                UpdateOptions();
            }
            catch (Exception e) { if (lastError != e.Message) Notice(e.Message); }
            finally { filling = false; refreshing = false; }
        }
        private void RefreshSimulacao()
        {
            try
            {
                rows = LerSimulacao();
                FillList();
                load.Enabled = false;
                toggle.Enabled = false;
                toggle.Text = "Ligar visual";
                clients.Enabled = false;
                if (DateTime.UtcNow - lastErrorTime > TimeSpan.FromSeconds(3))
                    status.Text = "  Modo simulaÃ§Ã£o  â€¢  Edite simulacao.json para mudar as entidades  (" + rows.Count + " entidades carregadas)";
            }
            catch (Exception e) { Notice(e.Message); }
        }
        private List<EntityRow> LerSimulacao()
        {
            if (!System.IO.File.Exists(SimulacaoPath)) {
                Notice("simulacao.json nÃ£o encontrado em: " + SimulacaoPath);
                return new List<EntityRow>();
            }
            Dictionary<string,object> data;
            using (var stream = new System.IO.FileStream(SimulacaoPath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
            using (var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8))
                data = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string,object>>(reader.ReadToEnd());
            var result = new List<EntityRow>();
            if (data == null || !data.ContainsKey("entidades")) return result;
            foreach (var item in (System.Collections.IEnumerable)data["entidades"])
            {
                var row = item as Dictionary<string,object>; if (row == null) continue;
                result.Add(new EntityRow {
                    Id       = row.ContainsKey("id")         ? Convert.ToString(row["id"]) : "0x0",
                    Name     = row.ContainsKey("nome")        ? Convert.ToString(row["nome"]) : "",
                    NameKey  = row.ContainsKey("chave_nome")  ? Convert.ToString(row["chave_nome"]) : "0x0",
                    Type     = row.ContainsKey("tipo")        ? Convert.ToString(row["tipo"]) : "nao_classificado",
                    Distance = row.ContainsKey("distancia")   ? Convert.ToDouble(row["distancia"]) : 0,
                    OnScreen = row.ContainsKey("na_tela")     && Convert.ToBoolean(row["na_tela"]),
                    Selected = row.ContainsKey("selecionado") && Convert.ToBoolean(row["selecionado"]),
                    Selectable = row.ContainsKey("selecionavel") && row["selecionavel"] != null ? (bool?)Convert.ToBoolean(row["selecionavel"]) : null,
                    Attackable = row.ContainsKey("atacavel")  && row["atacavel"]  != null ? (bool?)Convert.ToBoolean(row["atacavel"])  : null
                });
            }
            return result.OrderBy(x => x.Distance).ToList();
        }
        private void FillList()
        {
            var filtered = Filtered();
            string signature = String.Join("|", filtered.Select(r => r.Id + ":" + FilterLogic.Name(r) + ":" + FilterLogic.Category(r) + ":" + Math.Round(r.Distance) + ":" + r.OnScreen + ":" + r.Selected));
            total.Text = filtered.Count + " de " + rows.Count + " entidades próximas";
            if (signature == rowsSignature) return;
            string selected = list.SelectedItems.Count > 0 ? (string)list.SelectedItems[0].Tag : null;
            string top = list.TopItem == null ? null : (string)list.TopItem.Tag;
            bool sameKeys = list.Items.Count == filtered.Count;
            if (sameKeys) { for (int i = 0; i < filtered.Count; i++) { if ((string)list.Items[i].Tag != filtered[i].Id) { sameKeys = false; break; } } }
            if (sameKeys)
            {
                filling = true;
                for (int i = 0; i < filtered.Count; i++)
                {
                    EntityRow row = filtered[i]; ListViewItem item = list.Items[i];
                    string name = FilterLogic.Name(row);
                    if (item.Text != name) item.Text = name;
                    string kind = FilterLogic.Category(row);
                    if (item.SubItems[1].Text != kind) item.SubItems[1].Text = kind;
                    string dist = Math.Round(row.Distance).ToString() + " u";
                    if (item.SubItems[2].Text != dist) item.SubItems[2].Text = dist;
                    string onScreen = row.Selected ? "Alvo atual" : row.OnScreen ? "Sim" : "Não";
                    if (item.SubItems[3].Text != onScreen) item.SubItems[3].Text = onScreen;
                    Color color = row.Selected ? Color.FromArgb(232, 199, 115) : Ink;
                    if (item.ForeColor != color) item.ForeColor = color;
                    bool isChecked = chosen.Contains(row.Id);
                    if (item.Checked != isChecked) item.Checked = isChecked;
                }
                filling = false; rowsSignature = signature;
                return;
            }
            filling = true; list.BeginUpdate(); list.Items.Clear();
            foreach (EntityRow row in filtered)
            {
                string name = FilterLogic.Name(row);
                var item = new ListViewItem(name) { Tag = row.Id, Checked = chosen.Contains(row.Id) };
                string kind = FilterLogic.Category(row);
                item.SubItems.Add(kind); item.SubItems.Add(Math.Round(row.Distance).ToString() + " u"); item.SubItems.Add(row.Selected ? "Alvo atual" : row.OnScreen ? "Sim" : "Não");
                if (row.Selected) item.ForeColor = Color.FromArgb(232, 199, 115);
                list.Items.Add(item); item.Selected = row.Id == selected;
            }
            if (top != null) { var topItem = list.Items.Cast<ListViewItem>().FirstOrDefault(i => (string)i.Tag == top); if (topItem != null) list.TopItem = topItem; }
            list.EndUpdate(); filling = false; rowsSignature = signature;
        }
        private void Changed(bool refill)
        { if (filling) return; if (refill) { rowsSignature = ""; FillList(); } optionSignature = ""; UpdateOptions(); SavePreferences(); }
        private void SavePreferences()
        {
            preferences.Estilo = style.SelectedIndex; preferences.Tamanho = (int)markerSize.Value; preferences.Alcance = (int)range.Value;
            preferences.Recarregar = reload.Checked; preferences.Nomes = labels.Checked; preferences.Filtro = Convert.ToString(category.SelectedItem); preferences.Busca = search.Text;
            try { Directory.CreateDirectory(Path.GetDirectoryName(PreferencesFile)); File.WriteAllText(PreferencesFile, new JavaScriptSerializer().Serialize(preferences), new UTF8Encoding(false)); }
            catch (Exception e) { Notice("Não foi possível salvar as preferências: " + e.Message); }
        }
        private void UpdateOptions()
        { try { WriteOptions(Visible ? Handle : IntPtr.Zero); } catch (Exception e) { if (lastError != e.Message) Notice(e.Message); } }
        private void WriteOptions(IntPtr menu)
        {
            Client c = Current(); if (c == null || !ready || busy) return;
            bool restricted = onlyChosen.Checked || category.SelectedIndex != 0 || !String.IsNullOrWhiteSpace(search.Text);
            var allowed = Filtered().Where(r => !onlyChosen.Checked || chosen.Contains(r.Id)).Take(512).OrderBy(r => r.Id).ToList();
            var ids = allowed.Select(r => Convert.ToUInt64(r.Id.Substring(2), 16)).ToList();
            var names = allowed.Select(r => r.Name ?? "").ToList();
            string signature = c.Key + "/" + style.SelectedIndex + "/" + markerSize.Value + "/" + range.Value + "/" + labels.Checked + "/" + restricted + "/" + menu + "/" + String.Join(",", ids) + "/" + String.Join("|", names);
            if (signature == optionSignature) return;
            byte[] data = RadarOptions.Encode(c, ++revision, style.SelectedIndex, (int)markerSize.Value, (int)range.Value, labels.Checked, restricted, menu, 0, ids, names);
            RadarOptions.Write(c, data); optionSignature = signature;
        }
        private async Task LoadCurrent()
        {
            Client c = Current(); if (c == null || busy) return;
            lifecycle.Armed = true; lifecycle.Mark(c); busy = true; Notice("Carregando o visual…");
            try
            {
                var report = await Task.Run(() => Backend.Load(c, lifecycle.DesiredEnabled));
                Backend.Log("carregar-v3", c, report, true); optionSignature = ""; Notice("Visual pronto. F6 abre e oculta este menu.");
            }
            catch (Exception e) { Notice(e.Message); try { Backend.Log("carregar-v3", c, e.ToString(), false); } catch (Exception) { } }
            finally { busy = false; RefreshNow(); }
        }
        private void ToggleVisual()
        {
            Client c = Current(); if (c == null || busy || !ready) return;
            try { Backend.SetEnabled(c, !enabled); lifecycle.DesiredEnabled = !enabled; lifecycle.Armed = true; lifecycle.Mark(c); Notice(!enabled ? "Visual ligado." : "Visual desligado. F6 mantém a lista acessível."); RefreshNow(); }
            catch (Exception e) { Notice(e.Message); }
        }
        internal static object TestFilters()
        {
            var unknown = new EntityRow { Id = "0x34800000001", Name = "", Type = "nao_classificado", Distance = 12 };
            if (FilterLogic.Match(unknown, "Mobs/monstros", "")) throw new Exception("Desconhecido não pode virar monstro.");
            if (FilterLogic.Match(unknown, "NPC", "")) throw new Exception("Prefixo de entidade não comprova NPC.");
            unknown.Attackable = true;
            if (FilterLogic.Match(unknown, "Mobs/monstros", "")) throw new Exception("Ser atacável não comprova monstro, inclusive em PvP.");
            unknown.Selectable = true;
            if (FilterLogic.Match(unknown, "Objeto", "")) throw new Exception("Seleção não comprova objeto.");
            var cases = new[] { new[] { "monstro", "Mobs/monstros" }, new[] { "npc", "NPC" }, new[] { "realnpc", "RealNPC" }, new[] { "objeto", "Objeto" }, new[] { "jogador", "Jogador" } };
            foreach (var pair in cases) {
                var row = new EntityRow { Id = "0x1", Name = "Example", Type = pair[0] };
                if (!FilterLogic.Match(row, pair[1], "EXamp")) throw new Exception("Tipo automático e busca: " + pair[0]);
                if (FilterLogic.Match(row, pair[1], "ausente")) throw new Exception("Busca sem correspondência.");
            }
            var data = RadarOptions.Encode(new Client { Pid = 7, Started = 123456 }, 1, 0, 100, 80, false, true, IntPtr.Zero, 0, new List<ulong> { 0x34800000001 });
            if (data.Length != 102460 || BitConverter.ToUInt64(data, 60) != 0x34800000001 || BitConverter.ToUInt64(data, 8) != 123456) throw new Exception("Protocolo binário.");
            using (var form = new GameMenu()) {
                if (!form.Controls.Contains(form.list) || !form.Controls.Contains(form.toggle) || !form.Controls.Contains(form.search) ||
                    !form.category.Items.Contains("RealNPC") || form.category.Items.Contains("Selecionáveis")) throw new Exception("Controles e categorias do menu.");
                form.tray.Dispose();
            }
            return new { ok = true, testes = 16, captura_tela = false, descricao = "Categorias automáticas, dados desconhecidos, busca, opções binárias e criação do menu." };
        }
    }
}
