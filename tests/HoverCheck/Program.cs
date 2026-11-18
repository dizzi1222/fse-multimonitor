using System.Reflection;
using System.Drawing;
using System.Windows.Forms;
class Check {
[STAThread] static void Main() {
var a=Assembly.Load("FseMultiMonitor");
var t=a.GetType("FseMultiMonitor.WindowThumbnailPreview")!;
var w=a.GetType("FseMultiMonitor.Inspector+WindowState")!;
using var form=(Form)Activator.CreateInstance(t, BindingFlags.Instance|BindingFlags.NonPublic, null, new object[]{Array.CreateInstance(w,0),1,false},null)!;
var flags=BindingFlags.Instance|BindingFlags.NonPublic;
void Call(string name,params object[] args)=>t.GetMethod(name,flags)!.Invoke(form,args);
var state=typeof(Control).GetMethod("GetState",flags)!;
var visible=Enum.Parse(state.GetParameters()[0].ParameterType,"Visible");
bool Shown(string field)=>(bool)state.Invoke(t.GetField(field,flags)!.GetValue(form),new[]{visible})!;
void Assert(bool expected,string label) {foreach(var f in new[]{"_exitButton","_backButton","_fullScreenButton","_closeButton"}) if(Shown(f)!=expected) throw new Exception(label+" "+f);Console.WriteLine("PASS "+label);}
var b=(Button)t.GetField("_fullScreenButton",flags)!.GetValue(form)!;
var over=new Point(b.Left+3,b.Top+3);
Call("UpdateChromeHover",over);Assert(true,"fitted controls visible");
Call("ToggleFullScreen");Assert(false,"fullscreen starts clean");
Call("UpdateChromeHover",over);Assert(false,"entry click does not reveal controls");
Call("UpdateChromeHover",new Point(20,200));Assert(false,"away stays hidden");
Call("UpdateChromeHover",over);Assert(true,"hover reveals controls");
Call("UpdateChromeHover",new Point(b.Left-60,b.Top+3));Assert(true,"moving between buttons stays visible");
Call("UpdateChromeHover",new Point(-1,0));Assert(false,"leaving monitor hides controls");
Call("ToggleFullScreen");Assert(true,"fitted restores controls");
if(form.Controls.OfType<Label>().Any(x=>x.Text.Contains("oclus",StringComparison.OrdinalIgnoreCase)))throw new Exception("occlusion badge remains");
Call("Cleanup");
} }
