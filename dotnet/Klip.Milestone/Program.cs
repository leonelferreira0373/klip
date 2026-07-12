using System; using System.Collections.Generic; using System.Linq; using System.IO; using SkiaSharp;
using Klip.Engine; using Klip.Model;
// replicate the timeline drawing to verify the visual
var layers = new List<Layer>{
  new("ball", MorphTrack.Static(Shapes.Circle(60)), 0xFF6D5EF6,
     PosX: Track.Of(new Keyframe(0,-200), new Keyframe(1.5,200)),
     Scale: Track.Of(new Keyframe(0,0), new Keyframe(0.6,1))),
  new("bar", MorphTrack.Static(Shapes.Rect(120,20)), 0xFFF5B82E,
     Rotation: Track.Of(new Keyframe(0.3,0), new Keyframe(2,180)), PosY: Track.Const(100)),
  new("txt", MorphTrack.Static("M 0 0 L 1 0 Z"), 0xFF232326,
     Opacity: Track.Of(new Keyframe(1,0), new Keyframe(1.4,1), new Keyframe(2.5,1))),
};
double D=3.0, previewT=1.2; int W=900,H=136;
IEnumerable<double> KeyTimes(Layer l){ foreach(var tr in new[]{l.PosX,l.PosY,l.Rotation,l.Scale,l.ScaleX,l.ScaleY,l.SkewX,l.BlurRadius,l.Opacity,l.TrimStart,l.TrimEnd}) if(tr is {Keys.Count:>0}) foreach(var k in tr.Keys) yield return k.Time; }
using var surf=SKSurface.Create(new SKImageInfo(W,H,SKColorType.Rgba8888,SKAlphaType.Premul));
var c=surf.Canvas; c.Clear(new SKColor(0xFFFBFBFA));
float G=118, trackW=W-G-8; int n=layers.Count; float rowH=Math.Min(26f,(H-22f)/n); float top=22;
float Xt(double t)=>G+(float)(t/D)*trackW;
using var tick=new SKPaint{Color=new SKColor(0xFFDDDDDA),IsAntialias=true,StrokeWidth=1};
using var lbl=new SKPaint{Color=new SKColor(0xFF9A9A97),IsAntialias=true}; using var lf=new SKFont{Size=9.5f};
using var nm=new SKPaint{Color=new SKColor(0xFF4A4A47),IsAntialias=true}; using var nf=new SKFont{Size=10.5f};
using var dot=new SKPaint{Color=new SKColor(0xFF6D5EF6),IsAntialias=true};
for(double t=0;t<=D+1e-6;t+=0.5){float x=Xt(t); c.DrawLine(x,0,x,H,tick); c.DrawText($"{t:0.0}s",x+3,12,lf,lbl);}
c.DrawLine(G,0,G,H,tick);
for(int i=0;i<n;i++){ var L=layers[n-1-i]; float y=top+i*rowH; if(i%2==1){using var rb=new SKPaint{Color=new SKColor(0x08000000)};c.DrawRect(0,y,W,rowH,rb);}
  c.DrawText(L.Name,8,y+rowH*0.62f,nf,nm); float cy=y+rowH/2; foreach(var kt in KeyTimes(L).Distinct()) c.DrawCircle(Xt(kt),cy,3.2f,dot); }
using var ph=new SKPaint{Color=new SKColor(0xFFE0245E),IsAntialias=true,StrokeWidth=1.5f}; float px=Xt(previewT); c.DrawLine(px,0,px,H,ph);
using var pf=new SKPath(); pf.MoveTo(px-5,0);pf.LineTo(px+5,0);pf.LineTo(px,8);pf.Close(); using var pfp=new SKPaint{Color=new SKColor(0xFFE0245E),IsAntialias=true}; c.DrawPath(pf,pfp);
c.Flush(); using var img=surf.Snapshot(); using var d=img.Encode(SKEncodedImageFormat.Png,100);
File.WriteAllBytes(@"C:\Users\leone\klip\dotnet\_out\timeline.png", d.ToArray());
Console.WriteLine("timeline.png written");
