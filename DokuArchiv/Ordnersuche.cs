using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using Microsoft.Win32;

public partial class Archiv {
 sealed record Indexed(string Path,long Size,long Stamp,string Text,string Error);
 sealed record FolderIndex(List<string> Roots,List<Indexed> Files);
 static readonly string indexPath=Path.Combine(Path.GetDirectoryName(root)!,"DokuArchiv-Suchindex.json");
 static readonly HashSet<string> formats=new(StringComparer.OrdinalIgnoreCase){".txt",".md",".csv",".pdf",".docx",".png",".jpg",".jpeg",".bmp",".tif",".tiff"};
 static string Excerpt(string value,IEnumerable<string> terms,int length){
   int best=-1,score=-1;
   var words=terms.Where(t=>t.Length>0).Distinct().ToArray();
   foreach(var word in words){
     int hit=value.IndexOf(word,StringComparison.OrdinalIgnoreCase);
     if(hit<0)continue;
     int pos=Math.Max(0,hit-60);
     var candidate=value.AsSpan(pos,Math.Min(length,value.Length-pos)).ToString();
     int relevance=words.Count(t=>candidate.Contains(t,StringComparison.OrdinalIgnoreCase));
     if(relevance>score){best=pos;score=relevance;}
   }
   int start=Math.Max(0,best);
   return value.Substring(start,Math.Min(length,value.Length-start));
 }
 static void OpenIndexed(string path,bool explorer){
   try{
     if(!File.Exists(path))throw new IOException("Datei nicht mehr erreichbar. Bitte den Index aktualisieren.");
     if(explorer)Process.Start(new ProcessStartInfo("explorer.exe","/select,\""+path+"\""){UseShellExecute=false});
     else if(formats.Contains(Path.GetExtension(path)))Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
   }catch(Exception ex){MessageBox.Show(ex.Message,"Fundstelle öffnen");}
 }
 static async Task<string> ReadIndexed(string path,CancellationToken token){
   switch(Path.GetExtension(path).ToLowerInvariant()){
     case ".txt": case ".md": case ".csv": return await File.ReadAllTextAsync(path,token);
     case ".docx":
       using(var zip=ZipFile.OpenRead(path)){
         using var stream=(zip.GetEntry("word/document.xml")??throw new IOException("Dokumentinhalt fehlt")).Open();
         var doc=XDocument.Load(stream);
         XNamespace w="http://schemas.openxmlformats.org/wordprocessingml/2006/main";
         return string.Join("\n",doc.Descendants(w+"p").Select(p=>string.Concat(p.Descendants(w+"t").Select(t=>t.Value))));
       }
     default:
       var info=new ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8};
       foreach(var arg in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",Path.Combine(AppContext.BaseDirectory,"Erkennen.ps1"),path})info.ArgumentList.Add(arg);
       using(var proc=Process.Start(info)!){
         using var cancel=token.Register(()=>{try{proc.Kill(true);}catch(InvalidOperationException){}});
         var stdout=proc.StandardOutput.ReadToEndAsync();var stderr=proc.StandardError.ReadToEndAsync();
         await proc.WaitForExitAsync(token);await stderr;
         if(proc.ExitCode!=0)throw new IOException("Texterkennung fehlgeschlagen");
         return await stdout;
       }
   }
 }
 void FolderSearch(){
   var index=File.Exists(indexPath)?JsonSerializer.Deserialize<FolderIndex>(File.ReadAllText(indexPath))!:new FolderIndex(new(),new());
   var win=new Window{Title="Ordner durchsuchen",Owner=this,Width=940,Height=590,WindowStartupLocation=WindowStartupLocation.CenterOwner};
   var panel=new DockPanel{Margin=new Thickness(14)};win.Content=panel;
   var head=new StackPanel();DockPanel.SetDock(head,Dock.Top);panel.Children.Add(head);
   var bar=new WrapPanel();head.Children.Add(bar);
   Button Btn(string name){var b=new Button{Content=name,Margin=new Thickness(2),Padding=new Thickness(7,4,7,4)};bar.Children.Add(b);return b;}
   var add=Btn("Suchordner hinzufügen");var update=Btn("Index aktualisieren");var ai=Btn("Ordner fragen");var open=Btn("Datei öffnen");var explorer=Btn("Im Explorer anzeigen");
   var paths=new TextBlock{TextWrapping=TextWrapping.Wrap};head.Children.Add(paths);
   head.Children.Add(new Label{Content="Dateiname, Pfad oder Inhalt"});var query=new TextBox();head.Children.Add(query);
   var state=new TextBlock();DockPanel.SetDock(state,Dock.Bottom);panel.Children.Add(state);
   var grid=new DataGrid{AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false};panel.Children.Add(grid);
   foreach(var col in new[]{("Dateiname","Name",180),("Fundstelle","Snippet",350),("Vollständiger Pfad","Path",450)})
     grid.Columns.Add(new DataGridTextColumn{Header=col.Item1,Binding=new System.Windows.Data.Binding(col.Item2),Width=col.Item3});
   var matched=new List<Indexed>();
   string? Selected()=>grid.SelectedItem?.GetType().GetProperty("Path")?.GetValue(grid.SelectedItem) as string;
   void Show(){
     var words=query.Text.Split(' ',StringSplitOptions.RemoveEmptyEntries);
     matched=index.Files.Where(e=>words.All(t=>e.Path.Contains(t,StringComparison.OrdinalIgnoreCase)||e.Text.Contains(t,StringComparison.OrdinalIgnoreCase))).ToList();
     grid.ItemsSource=matched.Select(e=>new{Name=Path.GetFileName(e.Path),e.Path,Snippet=e.Error==""?Excerpt(e.Text,words,220).Replace('\n',' '):e.Error}).ToList();
     paths.Text=string.Join("  ·  ",index.Roots);state.Text=$"{matched.Count} Treffer · {index.Files.Count} Dateien im Index";
   }
   query.TextChanged+=(_,_)=>Show();Show();
   open.Click+=(_,_)=>{if(Selected() is string p)OpenIndexed(p,false);};
   explorer.Click+=(_,_)=>{if(Selected() is string p)OpenIndexed(p,true);};
   grid.MouseDoubleClick+=(_,_)=>{if(Selected() is string p)OpenIndexed(p,false);};
   ai.Click+=(_,_)=>Ask(index.Files.Select(e=>new Eintrag{Titel=Path.GetFileName(e.Path),Originalname=e.Path,Inhalt=e.Text,Zeitpunkt=new DateTimeOffset(new DateTime(e.Stamp,DateTimeKind.Utc))}).ToList(),query.Text);
   using var cts=new CancellationTokenSource();win.Closed+=(_,_)=>cts.Cancel();
   async Task Scan(List<string> roots){
     bar.IsEnabled=false;query.IsEnabled=false;
     try{
       var progress=new Progress<string>(msg=>state.Text=msg);
       var result=await Task.Run(async ()=>{
         var old=index.Files.ToDictionary(e=>e.Path,StringComparer.OrdinalIgnoreCase);
         var fresh=new Dictionary<string,Indexed>(StringComparer.OrdinalIgnoreCase);
         var opts=new EnumerationOptions{RecurseSubdirectories=true,IgnoreInaccessible=true,AttributesToSkip=FileAttributes.ReparsePoint|FileAttributes.System};
         foreach(var dir in roots){
           if(!Directory.Exists(dir))throw new IOException("Ordner nicht erreichbar: "+dir);
           foreach(var path in Directory.EnumerateFiles(dir,"*",opts)){
             cts.Token.ThrowIfCancellationRequested();
             if(!formats.Contains(Path.GetExtension(path))||fresh.ContainsKey(path)||Path.GetFileName(path).StartsWith("~$"))continue;
             var file=new FileInfo(path);
             long stamp=0,size=0;
             try{size=file.Length;stamp=file.LastWriteTimeUtc.Ticks;
               if(old.TryGetValue(path,out var cached)&&cached.Size==size&&cached.Stamp==stamp&&cached.Error==""){fresh[path]=cached;continue;}
               ((IProgress<string>)progress).Report("Lese "+path);
               fresh[path]=new(path,size,stamp,await ReadIndexed(path,cts.Token),"");
             }catch(Exception ex) when(ex is not OperationCanceledException){fresh[path]=new(path,size,stamp,"",ex.Message);}
           }
         }
         return new FolderIndex(roots,fresh.Values.ToList());
       },cts.Token);
       cts.Token.ThrowIfCancellationRequested();Save(indexPath,result);index=result;Show();
     }catch(OperationCanceledException){}catch(Exception ex){MessageBox.Show(ex.Message,"Ordnerindex");}finally{bar.IsEnabled=true;query.IsEnabled=true;}
   }
   add.Click+=async(_,_)=>{var chooser=new OpenFolderDialog();if(chooser.ShowDialog(win)==true)await Scan(index.Roots.Append(chooser.FolderName).Distinct(StringComparer.OrdinalIgnoreCase).ToList());};
   update.Click+=async(_,_)=>await Scan(index.Roots);
   win.ShowDialog();
 }
}