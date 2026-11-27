using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

public class Eintrag {
 public Guid Id {get;set;}=Guid.NewGuid();
 public DateTimeOffset Zeitpunkt {get;set;}=DateTimeOffset.Now;
 public string Datum => Zeitpunkt.LocalDateTime.ToString("dd.MM.yyyy HH:mm");
 public string Art {get;set;}="Notiz";
 public string Titel {get;set;}="";
 public string Inhalt {get;set;}="";
 public string Kurzinfo {get;set;}="";
 public string Datei {get;set;}="";
 public string Originalname {get;set;}="";
 public string Hash {get;set;}="";
 public string Tags {get;set;}="";
 public bool Entfernt {get;set;}
}
public class Archiv : Window {
 static readonly string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AssystDokuArchiv");
 static readonly string catalog=Path.Combine(root,"katalog.json");
 readonly string settings=Path.Combine(Path.GetDirectoryName(root)!,"AssystDokuArchiv-Ziele.json");
 List<Eintrag> entries=File.Exists(catalog)?JsonSerializer.Deserialize<List<Eintrag>>(File.ReadAllText(catalog))!:new();
 readonly TextBox search=new(), title=new(), tags=new(), summary=new(){AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=70}, body=new(){AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
 readonly DataGrid grid=new(){AutoGenerateColumns=false,IsReadOnly=true,SelectionMode=DataGridSelectionMode.Single};
 readonly Image image=new(){MaxHeight=190,Stretch=Stretch.Uniform};
 readonly CheckBox trash=new(){Content="Papierkorb",Margin=new Thickness(8)};
 readonly TextBlock status=new();
 bool busy;
 Eintrag? Current=>grid.SelectedItem as Eintrag;
 [STAThread] public static void Main(){
 using var mutex=new System.Threading.Mutex(true,"Local\\AssystDokuArchiv_v1",out bool first);
 if(!first){MessageBox.Show("Das Archiv ist bereits geöffnet.");return;}
 try{new Application().Run(new Archiv());}catch(Exception ex){MessageBox.Show(ex.Message,"Archiv konnte nicht geöffnet werden");}
 }
 static void Save<T>(string path,T value){
 string tmp=path+".tmp"; File.WriteAllText(tmp,JsonSerializer.Serialize(value));File.Move(tmp,path,true);
 }
 void Commit()=>Save(catalog,entries);
 public Archiv(){
 Directory.CreateDirectory(root);Title="DokuArchiv";Width=1220;Height=780;MinWidth=760;MinHeight=540;
 FontFamily=new FontFamily("Inter, Segoe UI");Background=Brushes.WhiteSmoke;
 var layout=new DockPanel{Margin=new Thickness(14)}; Content=layout;
 var bar=new WrapPanel();DockPanel.SetDock(bar,Dock.Top);layout.Children.Add(bar);
 Button Action(Panel panel,string label,Func<Task> action){
 var b=new Button{Content=label,Padding=new Thickness(9,5,9,5),Margin=new Thickness(2)};panel.Children.Add(b);
 b.Click+=async(_,_)=>await Run(action);return b;}
 Action(bar,"Dateien hinzufügen",async()=>{var d=new OpenFileDialog{Multiselect=true,Filter="Dokumente und Bilder|*.pdf;*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.txt;*.md"};if(d.ShowDialog()==true)await Import(d.FileNames);});
 Action(bar,"Neue Notiz",()=>{Note("");return Task.CompletedTask;});
 Action(bar,"Einfügen",Paste);
 Action(bar,"Sicherungsziel hinzufügen",()=>{var d=new OpenFolderDialog{Title="Synchronisierten OneDrive-, Nextcloud- oder Google-Drive-Ordner wählen"};if(d.ShowDialog()==true){if(Path.GetFullPath(d.FolderName).StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new IOException("Bitte ein Ziel außerhalb des Archivs wählen.");Save(settings,Targets().Append(d.FolderName).Distinct(StringComparer.OrdinalIgnoreCase));}return Task.CompletedTask;});
 Action(bar,"Jetzt sichern",Backup);
 Action(bar,"Sicherung einlesen",Restore);
 var filters=new DockPanel{Margin=new Thickness(0,10,0,10)};DockPanel.SetDock(filters,Dock.Top);layout.Children.Add(filters);
 filters.Children.Add(new Label{Content="Suche"});DockPanel.SetDock(trash,Dock.Right);filters.Children.Add(trash);filters.Children.Add(search);
 DockPanel.SetDock(status,Dock.Bottom);layout.Children.Add(status);
 var pane=new DockPanel{Width=340,Margin=new Thickness(12,0,0,0)};DockPanel.SetDock(pane,Dock.Right);layout.Children.Add(pane);
 var fields=new StackPanel();DockPanel.SetDock(fields,Dock.Top);pane.Children.Add(fields);
 foreach(var pair in new[]{("Titel",title),("Schlagwörter",tags),("Kurzinfo",summary)}){fields.Children.Add(new Label{Content=pair.Item1});fields.Children.Add(pair.Item2);}
 fields.Children.Add(image);fields.Children.Add(new Label{Content="Inhalt / erkannter Text"});
 var buttons=new WrapPanel();DockPanel.SetDock(buttons,Dock.Bottom);pane.Children.Add(buttons);pane.Children.Add(body);
 var saved = new TextBlock { Foreground=Brushes.DarkGreen, FontWeight=FontWeights.SemiBold, Margin=new Thickness(4,2,4,4) };
 DockPanel.SetDock(saved,Dock.Bottom);pane.Children.Insert(pane.Children.IndexOf(body),saved);
 grid.SelectionChanged+=(_,_)=>saved.Text="";
 Action(buttons,"Speichern",()=>{if(Current is {} e){e.Titel=title.Text;e.Tags=tags.Text;e.Inhalt=body.Text;e.Kurzinfo=summary.Text;Commit();Refresh(e);grid.UnselectAll();ShowEntry();saved.Text="✓ Gespeichert um "+DateTime.Now.ToString("HH:mm:ss");}return Task.CompletedTask;});
 Action(buttons,"Original öffnen",()=>{if(Current is {Datei.Length:>0} e)Process.Start(new ProcessStartInfo(Path.Combine(root,e.Datei)){UseShellExecute=true});return Task.CompletedTask;});
 Action(buttons,"Papierkorb ↔ Archiv",()=>{if(Current is {} e){e.Entfernt=!e.Entfernt;Commit();Refresh();}return Task.CompletedTask;});
 layout.Children.Add(grid);
 foreach(var col in new[]{("Hinzugefügt","Datum",135),("Art","Art",95),("Titel","Titel",210),("Kurzinfo","Kurzinfo",300)})grid.Columns.Add(new DataGridTextColumn{Header=col.Item1,Binding=new System.Windows.Data.Binding(col.Item2),Width=col.Item3});
 grid.SelectionChanged+=(_,_)=>ShowEntry();search.TextChanged+=(_,_)=>Refresh();trash.Click+=(_,_)=>Refresh();
 AllowDrop=true;Drop+=async(_,e)=>{if(e.Data.GetData(DataFormats.FileDrop) is string[] files)await Run(()=>Import(files));};
 PreviewKeyDown+=async(_,e)=>{if(e.Key==System.Windows.Input.Key.V && System.Windows.Input.Keyboard.Modifiers==System.Windows.Input.ModifierKeys.Control && System.Windows.Input.Keyboard.FocusedElement is not TextBox){e.Handled=true;await Run(Paste);}};
 Closing+=(_,e)=>{if(busy)e.Cancel=true;};Refresh();
 }
 async Task Run(Func<Task> action){if(busy)return;busy=true;IsEnabled=false;try{await action();}catch(Exception ex){MessageBox.Show(this,ex.Message,"DokuArchiv");}finally{IsEnabled=true;busy=false;}}
 void Refresh(Eintrag? select=null){
 var terms=search.Text.Split(' ',StringSplitOptions.RemoveEmptyEntries);
 var rows=entries.Where(e=>e.Entfernt==(trash.IsChecked==true)&&terms.All(t=>string.Join(" ",e.Titel,e.Art,e.Datum,e.Originalname,e.Tags,e.Kurzinfo,e.Inhalt).Contains(t,StringComparison.CurrentCultureIgnoreCase))).OrderByDescending(e=>e.Zeitpunkt).ToList();
 grid.ItemsSource=rows;grid.SelectedItem=select!=null&&rows.Contains(select)?select:rows.FirstOrDefault();status.Text=rows.Count+" Einträge";ShowEntry();
 }
 void ShowEntry(){var e=Current;title.Text=e?.Titel??"";summary.Text=e?.Kurzinfo??"";tags.Text=e?.Tags??"";body.Text=e?.Inhalt??"";image.Source=null;
 if(e!=null&&IsImage(e.Datei)){try{var bmp=new BitmapImage();bmp.BeginInit();bmp.CacheOption=BitmapCacheOption.OnLoad;bmp.DecodePixelWidth=600;bmp.UriSource=new Uri(Path.Combine(root,e.Datei));bmp.EndInit();image.Source=bmp;}catch{}}
 }
 static bool IsImage(string file)=>new[]{".png",".jpeg",".jpg",".bmp",".tif",".tiff"}.Contains(Path.GetExtension(file).ToLowerInvariant());
 void Add(Eintrag e){entries.Add(e);try{Commit();}catch{entries.Remove(e);throw;}trash.IsChecked=false;search.Clear();Refresh(e);}
 void Note(string text){var e=new Eintrag{Inhalt=text};Describe(e,"Neue Notiz");Add(e);body.Focus();}
 static void Describe(Eintrag e,string fallback){
 var lines=e.Inhalt.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
 var subject=Regex.Match(e.Inhalt,@"(?im)^\s*(?:Betreff|Subject)\s*:\s*(.+)$");
 if(subject.Success||Regex.IsMatch(e.Inhalt,@"(?im)^(Von|From)\s*:"))e.Art="E-Mail";
 else if(Regex.IsMatch(e.Inhalt,@"(?i)\bRechnung(?:snummer)?\b"))e.Art="Rechnung";
 string heading=subject.Success?subject.Groups[1].Value.Trim():lines.FirstOrDefault()??fallback;
 e.Titel=heading[..Math.Min(100,heading.Length)];
 var excerpt=string.Join(" ",lines.Where(s=>!Regex.IsMatch(s,@"(?i)^(Von|From|An|To|Betreff|Subject|Gesendet|Sent):")).Take(5));
 var details = new List<string>();
   void AddField(string label, string pattern) {
     var match = Regex.Match(e.Inhalt, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
     if (match.Success) details.Add(label + ": " + match.Groups[1].Value.Trim());
   }
   if (e.Art == "E-Mail") {
     AddField("Von", @"^[ \t]*(?:Von|From)[ \t]*:[ \t]*([^\r\n]+)");
   } else if (e.Art == "Rechnung") {
     AddField("Rechnungsnr.", @"\b(?:Rechnungsnummer|Rechnungs[- ]?Nr\.?)[ \t]*[:#]?[ \t]*([A-Z0-9][A-Z0-9/._-]*\d[A-Z0-9/._-]*|\d)");
     AddField("Gesamt", @"\b(?:Gesamtbetrag|Rechnungsbetrag|Endbetrag|Bruttobetrag)[ \t]*:?[ \t]*((?:EUR[ \t]*|€[ \t]*)?\d[\d.,]*[ \t]*(?:EUR|€)?)");
     AddField("Fällig am", @"\b(?:fällig am|zahlbar bis)[ \t]*:?[ \t]*(\d{1,2}\.\d{1,2}\.\d{2,4})");
   }
   if (details.Count > 0) excerpt = string.Join(" · ", details) + " — " + excerpt;
   e.Kurzinfo=excerpt[..Math.Min(280,excerpt.Length)];
 }
 async Task Paste(){
 if(Clipboard.ContainsFileDropList()){await Import(Clipboard.GetFileDropList().Cast<string>());return;}
 if(Clipboard.ContainsImage()){string f=Path.Combine(Path.GetTempPath(),"Screenshot-"+Guid.NewGuid()+".png");try{var enc=new PngBitmapEncoder();enc.Frames.Add(BitmapFrame.Create(Clipboard.GetImage()));using(var s=File.Create(f))enc.Save(s);await Import(new[]{f});}finally{File.Delete(f);}return;}
 if(Clipboard.ContainsText())Note(Clipboard.GetText());
 }
 async Task Import(IEnumerable<string> paths){
 foreach(var path in paths){string ext=Path.GetExtension(path).ToLowerInvariant();
 if(!IsImage(path)&&ext!=".pdf"&&ext!=".txt"&&ext!=".md")continue;
 var e=new Eintrag{Originalname=Path.GetFileName(path),Art=IsImage(path)?"Bild":ext==".pdf"?"PDF":"Text"};
 using(var s=File.OpenRead(path))e.Hash=Convert.ToHexString(await SHA256.HashDataAsync(s));
 if(entries.FirstOrDefault(x=>x.Hash==e.Hash) is {} existing){trash.IsChecked=existing.Entfernt;search.Clear();Refresh(existing);continue;}
 e.Datei=e.Id.ToString("N")+ext;string dest=Path.Combine(root,e.Datei);File.Copy(path,dest);
 status.Text="Lese "+e.Originalname;
 string warning="";
 try{if(ext is ".txt" or ".md")e.Inhalt=await File.ReadAllTextAsync(dest);
 else {var psi=new ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8};
 foreach(var arg in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",Path.Combine(AppContext.BaseDirectory,"Erkennen.ps1"),dest})psi.ArgumentList.Add(arg);
 using var p=Process.Start(psi)!;var output=p.StandardOutput.ReadToEndAsync();var error=p.StandardError.ReadToEndAsync();await p.WaitForExitAsync();e.Inhalt=await output;if(p.ExitCode!=0)warning="Texterkennung fehlgeschlagen: "+(await error).Split('\n')[0];}
 }catch(Exception ex){warning="Texterkennung fehlgeschlagen: "+ex.Message;}
 Describe(e,Path.GetFileNameWithoutExtension(path));if(warning!="")e.Kurzinfo=warning;else if(e.Inhalt.Trim()=="")e.Kurzinfo="Kein lesbarer Text erkannt.";Add(e);
 }}
 List<string> Targets()=>File.Exists(settings)?JsonSerializer.Deserialize<List<string>>(File.ReadAllText(settings))!:new();
 async Task Backup(){var targets=Targets();if(targets.Count==0)throw new IOException("Bitte zuerst einen Sicherungsordner wählen.");Commit();
 string name="DokuArchiv-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6]+".zip";
 var reports=new List<string>();foreach(var dir in targets){string tmp=Path.Combine(dir,name+".tmp");try{await Task.Run(()=>ZipFile.CreateFromDirectory(root,tmp));File.Move(tmp,Path.Combine(dir,name));reports.Add(dir+" – abgelegt");}catch(Exception ex){reports.Add(dir+" – "+ex.Message);}finally{if(File.Exists(tmp))File.Delete(tmp);}}MessageBox.Show(string.Join("\n",reports),"Sicherung");}
 async Task Restore(){var d=new OpenFileDialog{Filter="DokuArchiv-Sicherung|*.zip"};if(d.ShowDialog()!=true)return;
 using var zip=ZipFile.OpenRead(d.FileName);using var stream=(zip.GetEntry("katalog.json")??throw new IOException("Katalog fehlt.")).Open();
 var restored=await JsonSerializer.DeserializeAsync<List<Eintrag>>(stream)??throw new IOException("Katalog ungültig.");
 foreach(var e in restored){if(entries.Any(x=>x.Id==e.Id))continue;if(e.Datei!=""){
 var ext=Path.GetExtension(e.Datei);if(e.Datei!=e.Id.ToString("N")+ext||(!IsImage(e.Datei)&&ext!=".pdf"&&ext!=".txt"&&ext!=".md"))throw new IOException("Ungültiger Dateieintrag.");
 var source=zip.GetEntry(e.Datei)??throw new IOException("Original fehlt.");string dest=Path.Combine(root,e.Datei);source.ExtractToFile(dest+".tmp",true);
 using(var s=File.OpenRead(dest+".tmp"))if(Convert.ToHexString(await SHA256.HashDataAsync(s))!=e.Hash)throw new IOException("Prüfsumme fehlerhaft.");File.Move(dest+".tmp",dest,true);}
 Add(e);}Refresh();}
}
