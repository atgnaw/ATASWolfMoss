using OFT.Rendering;
using System.Drawing;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

internal sealed class RecordingRenderContext : RenderContext
{
    protected override string Name => "Tracker host test";
    private Rectangle _clip = new(0, 0, 800, 600);
    public List<(Rectangle Rect, Color Color)> Fills { get; } = [];
    public List<Rectangle> Outlines { get; } = [];
    public List<(Point From, Point To)> Lines { get; } = [];
    public List<string> Texts { get; } = [];
    public List<Point[]> SolidTriangles { get; } = [];
    public List<Point[]> HollowTriangles { get; } = [];
    public bool ThrowOnDraw { get; set; }
    public override Rectangle ClipBounds => _clip;
    public override int SetInterpolationMode(RenderInterpolationModes mode) => 0;
    public override int SetSmoothingMode(RenderSmoothingModes mode) => 0;
    public override int DrawLine(RenderPen pen, int x1, int y1, int x2, int y2) { Lines.Add((new(x1, y1), new(x2, y2))); return 0; }
    public override int DrawLines(RenderPen pen, Point[] points) => 0;
    public override int DrawRectangle(RenderPen pen, Rectangle rect) { Outlines.Add(rect); return 0; }
    public override int DrawRectangle(RenderPen pen, Rectangle rect, int radius) => 0;
    public override int DrawEllipse(RenderPen pen, Rectangle rect) => 0;
    public override int DrawPolygon(RenderPen pen, Point[] points) { HollowTriangles.Add((Point[])points.Clone()); return 0; }
    public override int Clear(Color color) => 0;
    public override int FillRectangle(Color brush, Rectangle rect) { if (ThrowOnDraw) throw new InvalidOperationException("Test draw failure"); Fills.Add((rect, brush)); return 0; }
    public override int FillRectangle(Color brush, Rectangle rect, int radius) => 0;
    public override int FillRectangle(Color brush1, Color brush2, Rectangle rect, bool vertical) => 0;
    public override int FillPolygon(Color brush, Point[] points) { SolidTriangles.Add((Point[])points.Clone()); return 0; }
    public override int FillPolygon(Color brushStart, Color brushEnd, Point[] points, bool vertical) => 0;
    public override int FillEllipse(Color brush, Rectangle rect) => 0;
    public override int FillPie(Color brush, Rectangle rect, float startAngle, float sweepAngle) => 0;
    public override int DrawString(string s, RenderFont font, Color brush, int x, int y) { Texts.Add(s); return 0; }
    public override int DrawString(string s, RenderFont font, Color brush, int x, int y, RenderStringFormat format) { Texts.Add(s); return 0; }
    public override int DrawString(string s, RenderFont font, Color brush, Rectangle rect) { Texts.Add(s); return 0; }
    public override int DrawString(string s, RenderFont font, Color brush, Rectangle rect, RenderStringFormat format) { Texts.Add(s); return 0; }
    public override Size MeasureString(string text, RenderFont font) => new(100, 20);
    public override Rectangle MeasureStringWithOverhang(string text, RenderFont font) => new(0, 0, 100, 20);
    public override int DrawStaticImage(Image image, Rectangle dest) => 0;
    public override int DrawStaticImage(IBitmapImage image, Rectangle dest) => 0;
    public override int DrawLayout(DrawingLayout layout, Point point = default) => 0;
    public override int SetClip(Rectangle rect) { _clip = rect; return 0; }
    public override int ResetClip() { _clip = new(0, 0, 800, 600); return 0; }
    public override int TranslateTransform(int dx, int dy) => 0;
    public override RenderContext CreateLayoutRenderContext(DrawingLayout layout) => this;
    public override int DrawSmoothedLine(RenderPen pen, Point[] points) => 0;
    public override int DrawQuadraticSpline(RenderPen pen, Point[] points) => 0;
}
