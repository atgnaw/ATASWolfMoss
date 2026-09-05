using System.Diagnostics;
using System.Drawing;
using WolfMoss.ATAS.FootprintOutline.Core;

var passed = 0; var failed = 0;
void Test(string name, Action check)
{
    try { check(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
void Equal<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected {expected}, got {actual}");
}
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

Test("Native automatic direction marker layout", () =>
{
    Equal(new ProfileLayout(110,89), ProfileGeometry.GetLayout(100,100m,true,0));
    Equal(new ProfileLayout(104,25), ProfileGeometry.GetLayout(100,30m,true,0));
    Equal(new ProfileLayout(103,11), ProfileGeometry.GetLayout(100,15.5m,true,0));
    Equal(new ProfileLayout(102,2), ProfileGeometry.GetLayout(100,5m,true,0));
});
Test("Explicit and disabled direction marker", () =>
{
    Equal(new ProfileLayout(107,92), ProfileGeometry.GetLayout(100,100m,true,7));
    Equal(new ProfileLayout(100,99), ProfileGeometry.GetLayout(100,100m,false,7));
    Equal(0, ProfileGeometry.GetLayout(100,5m,true,20).MaximumWidth);
});
Test("Proportion truncation, saturation, minimum pixel", () =>
{
    Equal(44, ProfileGeometry.GetWidth(500,1000,89));
    Equal(89, ProfileGeometry.GetWidth(1000,1000,89));
    Equal(89, ProfileGeometry.GetWidth(1500,1000,89));
    Equal(1, ProfileGeometry.GetWidth(0.01m,1000,89));
    Equal(0, ProfileGeometry.GetWidth(0,1000,89));
    Equal(0, ProfileGeometry.GetWidth(50,0,89));
    Equal(89, ProfileGeometry.GetWidth(decimal.MaxValue,1,89));
});
Test("Different candles share a single proportion", () =>
{
    var layout=ProfileGeometry.GetLayout(0,100m,false,0);
    var a=ProfileGeometry.GetRectangles([new(100,200),new(101,900)],1,5,layout,1000,p=>600-(int)p*5);
    var b=ProfileGeometry.GetRectangles([new(100,200),new(101,20)],1,5,layout,1000,p=>600-(int)p*5);
    Equal(a[0].Width,b[0].Width); Equal(19,a[0].Width);
    Equal(39,ProfileGeometry.GetWidth(200,500,99));
});
Test("Displayed bins copied without second aggregation", () =>
{
    var p=ProfileGeometry.CopyLevels([new(-0.5m,20),new(0,30),new(-0.5m,5),new(0.5m,0)]);
    Equal(2,p.Length); Equal(new PriceLevel(-0.5m,25),p[0]); Equal(new PriceLevel(0,30),p[1]);
});
Test("Fractional row height and native plus-one origin", () =>
{
    var r=ProfileGeometry.GetRectangles([new(10,50),new(11,75),new(12,100)],1,5.6m,new(7,99),100,p=>(int)(100m-p*5.6m));
    Equal(new Rectangle(7,45,49,5),r[0]); Equal(39,r[1].Top);
    Equal(45,r[1].Bottom); Equal(33,r[2].Top); Equal(39,r[2].Bottom);
});
Test("1/2/4 tick display aggregation", () =>
{
    foreach(var step in new[]{0.25m,0.5m,1m})
    {
        var r=ProfileGeometry.GetRectangles([new(100,10),new(100+step,20)],step,6,new(0,100),100,p=>100-(int)((p-100)/step)*6);
        Equal(101,r[0].Top); Equal(101,r[1].Bottom); Equal(1,ProfileGeometry.TraceOutline(r).Length);
    }
});
Test("Single row closed rectangle", () =>
{
    var p=ProfileGeometry.TraceOutline([new(10,20,8,6)]);
    Equal(1,p.Length);
    Assert(p[0].SequenceEqual(new[]{new Point(10,20),new(18,20),new(18,26),new(10,26),new(10,20)}),"Incorrect rectangle");
});
Test("Equal widths remove all internal separators", () =>
{
    var p=ProfileGeometry.TraceOutline([new(0,0,5,3),new(0,3,5,3),new(0,6,5,3)]);
    Equal(1,p.Length); Equal(5,p[0].Length);
});
Test("Multi-peak profile retains notches", () =>
{
    var p=ProfileGeometry.TraceOutline([new(0,0,8,2),new(0,2,2,2),new(0,4,9,2)]);
    Equal(1,p.Length); Assert(p[0].Contains(new Point(2,2))&&p[0].Contains(new Point(2,4)),"Notch lost");
});
Test("Missing levels remain disconnected", () =>
{
    var r=ProfileGeometry.GetRectangles([new(10,100),new(12,100)],1,5,new(0,100),100,p=>100-(int)p*5);
    Equal(2,ProfileGeometry.TraceOutline(r).Length);
});
Test("Overlapping screen rows form a union", () =>
{
    var r=new[]{new Rectangle(0,0,2,3),new(0,1,7,3),new(0,1,4,1)};
    var p=ProfileGeometry.TraceOutline(r); Equal(1,p.Length); CheckRaster(r,p);
});
Test("Empty and zero-area inputs", () =>
{
    Equal(0,ProfileGeometry.TraceOutline([]).Length);
    Equal(0,ProfileGeometry.TraceOutline([new(0,0,0,5)]).Length);
});
Test("500 randomized unions: coverage and all edges", () =>
{
    var random=new Random(7391);
    for(var i=0;i<500;i++)
    {
        var r=Enumerable.Range(0,random.Next(1,20)).Select(_=>new Rectangle(-3,random.Next(-8,22),random.Next(1,18),random.Next(1,7))).ToArray();
        CheckRaster(r,ProfileGeometry.TraceOutline(r));
    }
});
Test("Current bar replacement and removed history", () =>
{
    var s=new SnapshotStore();
    for(var i=0;i<100;i++) s.Set(i,100,1,[new(100,i+1)],s.Generation);
    Equal(100,s.Count);
    var revision=s.GetVisible(99,99)[0].Value.Revision;
    Assert(!s.Set(99,100,1,[new(100,100)],s.Generation),"Identical snapshot reported a change");
    Equal(revision,s.GetVisible(99,99)[0].Value.Revision);
    for(var i=0;i<1000;i++) s.Set(99,100,1,[new(100,i+1)],s.Generation);
    Equal(100,s.Count); Equal(1000m,s.GetVisible(99,99)[0].Value.Levels[0].Volume);
    s.Set(19,20,1,[new(100,50)],s.Generation);
    Equal(20,s.Count); Equal(5,s.GetVisible(15,99).Length);
});
Test("Visible snapshot buffer is reused without steady-state allocation", () =>
{
    var s=new SnapshotStore();
    for(var i=0;i<200;i++) s.Set(i,200,1,[new(100,i+1)],s.Generation);
    var buffer=new List<KeyValuePair<int,BarSnapshot>>();
    s.CopyVisible(25,174,buffer);
    var before=GC.GetAllocatedBytesForCurrentThread();
    for(var i=0;i<10_000;i++) s.CopyVisible(25,174,buffer);
    var allocated=GC.GetAllocatedBytesForCurrentThread()-before;
    Equal(0L,allocated);
    Equal(150,buffer.Count);
});
Test("Recalculation rejects pending obsolete data", () =>
{
    var s=new SnapshotStore();var old=s.Generation;s.Clear();
    s.Set(0,1,1,[new(100,50)],old);Equal(0,s.Count);
    s.Set(0,1,1,[new(100,80)],s.Generation);Equal(1,s.Count);
});
Test("Concurrent snapshots and price updates", () =>
{
    var s=new SnapshotStore();
    Parallel.For(0,2000,i=>
    {
        s.Set(i%10,10,1,[new(100,i+1)],s.Generation);
        foreach(var p in s.GetVisible(0,9)) Assert(p.Value.Levels.Length==1,"Partial snapshot");
    });
    Equal(10,s.Count);
});

void CheckRaster(Rectangle[] rectangles, Point[][] paths)
{
    bool Filled(double x,double y)=>rectangles.Any(r=>x>r.Left&&x<r.Right&&y>r.Top&&y<r.Bottom);
    foreach(var p in paths)
    {
        Assert(p.Length>=5&&p[0]==p[^1],"Open contour");
        for(var i=1;i<p.Length;i++)
        {
            var a=p[i-1];var b=p[i];
            Assert(a!=b&&(a.X==b.X||a.Y==b.Y),"Diagonal or degenerate edge");
            var distance=Math.Abs(a.X-b.X)+Math.Abs(a.Y-b.Y);
            for(var n=0;n<distance;n++)
            {
                var t=(n+0.5)/distance;
                var x=a.X+(b.X-a.X)*t;var y=a.Y+(b.Y-a.Y)*t;
                var dx=a.X==b.X?0.1:0;var dy=a.Y==b.Y?0.1:0;
                Assert(Filled(x-dx,y-dy)!=Filled(x+dx,y+dy),"Internal or nonexistent boundary");
            }
        }
    }
    for(var y=rectangles.Min(r=>r.Top)-1;y<=rectangles.Max(r=>r.Bottom);y++)
        for(var x=rectangles[0].Left-1;x<=rectangles.Max(r=>r.Right);x++)
            Equal(Filled(x+0.5,y+0.5),paths.Any(p=>Contains(p,x+0.5,y+0.5)));
}
static bool Contains(Point[] p,double x,double y)
{
    var inside=false;
    for(var i=1;i<p.Length;i++)
    {
        var a=p[i-1];var b=p[i];
        if((a.Y>y)!=(b.Y>y)&&x<(double)(b.X-a.X)*(y-a.Y)/(b.Y-a.Y)+a.X) inside=!inside;
    }
    return inside;
}
var watch=Stopwatch.StartNew();
var rows=Enumerable.Range(0,500).Select(i=>new Rectangle(0,i*2,1+i%100,2)).ToArray();
for(var i=0;i<200;i++) ProfileGeometry.TraceOutline(rows);
Console.WriteLine($"BENCH 200 x 500 rows: {watch.Elapsed.TotalMilliseconds:F1} ms (geometry only, not live ATAS FPS)");
Console.WriteLine($"RESULT {passed} passed, {failed} failed");
return failed==0?0:1;

