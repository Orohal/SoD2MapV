using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SoD2MapViewer;

/// <summary>Top-down (world X/Y) canvas with pan & zoom for map objects.</summary>
public sealed class MapCanvas : Control
{
    public IReadOnlyList<MapObject> Objects = Array.Empty<MapObject>();
    private float _zoom = 1f;               // pixels per world unit (world unit = UE cm)
    private PointF _center = new(0, 0);     // world coords at control center
    private int _selected = -1;
    private int _hover = -1;
    private bool _panning;
    private Point _panStart;
    private PointF _panStartCenter;

    public event Action<int>? SelectionChanged; // index of selected object, -1 = none

    /// <summary>Fired after a drag moved an object: (index, oldX, oldY). Editing only.</summary>
    public event Action<int, float, float>? ObjectMoved;

    /// <summary>When true, dragging an object moves it instead of panning the view.</summary>
    public bool EditingEnabled;

    private bool _dragging;
    private int _dragIndex = -1;
    private bool _dragMoved;
    private PointF _dragGrabOffset; // world offset mouse -> object at drag start
    private float _dragOldX, _dragOldY;

    private readonly Dictionary<string, Brush> _brushCache = new();
    private readonly ToolTip _tip = new() { InitialDelay = 300, ReshowDelay = 100 };

    private static readonly Color[] Palette =
    {
        Color.FromArgb(120, 190, 255), Color.FromArgb(255, 160, 100), Color.FromArgb(150, 255, 160),
        Color.FromArgb(255, 240, 120), Color.FromArgb(220, 160, 255), Color.FromArgb(120, 255, 255),
        Color.FromArgb(255, 130, 170), Color.FromArgb(180, 220, 255), Color.FromArgb(230, 230, 230),
        Color.FromArgb(160, 255, 200), Color.FromArgb(255, 200, 150), Color.FromArgb(200, 200, 255),
    };

    public MapCanvas()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(24, 26, 32);
        Cursor = Cursors.Cross;
    }

    public void SetObjects(IReadOnlyList<MapObject> objects)
    {
        Objects = objects;
        _selected = -1;
        _hover = -1;
        FitToObjects();
        Invalidate();
    }

    /// <summary>Re-fits the view so all objects are visible.</summary>
    public void Fit()
    {
        FitToObjects();
        Invalidate();
    }

    public void SelectIndex(int index, bool centerOnSelection = true)
    {
        _selected = index;
        if (centerOnSelection && _selected >= 0 && _selected < Objects.Count)
        {
            var o = Objects[_selected];
            _center = new PointF(o.X, o.Y);
        }
        Invalidate();
    }

    public IReadOnlyDictionary<string, Color> Legend()
    {
        return Objects
            .GroupBy(o => ClassCategory.From(o.ClassName))
            .ToDictionary(w => w.Key, w => ColorFor(w.Key));
    }

    private Brush BrushFor(string category)
    {
        if (!_brushCache.TryGetValue(category, out var b))
        {
            b = new SolidBrush(ColorFor(category));
            _brushCache[category] = b;
        }
        return b;
    }

    private static Color ColorFor(string category)
    {
        var idx = category.Aggregate(0, (h, s) => h * 31 + s);
        if (idx < 0) idx = -idx;
        return Palette[idx % Palette.Length];
    }

    public static Color GetPaletteColor(string category) => ColorFor(category);

    private void FitToObjects()
    {
        if (Objects.Count == 0) return;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var o in Objects)
        {
            if (!o.HasTransform) continue;
            if (o.X < minX) minX = o.X;
            if (o.Y < minY) minY = o.Y;
            if (o.X > maxX) maxX = o.X;
            if (o.Y > maxY) maxY = o.Y;
        }
        if (minX > maxX) return;
        _center = new PointF((minX + maxX) / 2f, (minY + maxY) / 2f);
        float span = MathF.Max(maxX - minX, maxY - minY);
        if (span <= 0) span = 1000;
        _zoom = MathF.Min(Width, Height) / span * 0.9f;
        if (!float.IsFinite(_zoom) || _zoom <= 0) _zoom = 0.01f;
    }

    private static float ClampF(float v, float min, float max) => v < min ? min : (v > max ? max : v);

    private PointF WorldToScreen(float x, float y) => new(
        Width / 2f + (x - _center.X) * _zoom,
        Height / 2f - (y - _center.Y) * _zoom); // Y up

    private PointF ScreenToWorld(float sx, float sy) => new(
        _center.X + (sx - Width / 2f) / _zoom,
        _center.Y - (sy - Height / 2f) / _zoom);

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // grid lines in 10 m steps
        const float gridStep = 1000f; // 10 meters in cm
        if (_zoom > 0.002f)
        {
            var tl = ScreenToWorld(0, 0);
            var br = ScreenToWorld(Width, Height);
            var x0 = MathF.Floor(tl.X / gridStep) * gridStep;
            var y0 = MathF.Floor(br.Y / gridStep) * gridStep;
            using var gridPen = new Pen(Color.FromArgb(34, 38, 46));
            for (float x = x0; x < br.X; x += gridStep)
                g.DrawLine(gridPen, WorldToScreen(x, 0).X, 0, WorldToScreen(x, 0).X, Height);
            for (float y = y0; y < tl.Y; y += gridStep)
                g.DrawLine(gridPen, 0, WorldToScreen(0, y).Y, Width, WorldToScreen(0, y).Y);
        }

        var dot = ClampF(0.2f * _zoom, 2.5f, 14f);
        for (int i = 0; i < Objects.Count; i++)
        {
            var o = Objects[i];
            if (!o.HasTransform) continue;
            var p = WorldToScreen(o.X, o.Y);
            if (p.X < -30 || p.Y < -30 || p.X > Width + 30 || p.Y > Height + 30) continue;

            var cat = ClassCategory.From(o.ClassName);
            var col = ColorFor(cat);
            if (i == _hover || i == _selected)
                col = Color.FromArgb(255, col);
            var b = BrushFor(cat);
            var size = dot;
            if (i == _selected) size = dot * 2f + 4f;
            else if (i == _hover) size = dot * 1.5f + 2f;
            g.FillRectangle(b, p.X - size / 2, p.Y - size / 2, size, size);

            if (i == _selected)
            {
                using var hl = new Pen(Color.White, 2f);
                g.DrawEllipse(hl, p.X - size, p.Y - size, size * 2, size * 2);
            }
        }

        using var hudFont = new Font("Segoe UI", 8.5f);
        using var hudBrush = new SolidBrush(Color.FromArgb(150, 200, 210));
        g.DrawString(
            $"zoom {_zoom:0.000} px/cm   center [{_center.X / 100:0.#} m / {_center.Y / 100:0.#} m]   " +
            $"hover: {(_hover >= 0 ? Objects[_hover].Name : "-")}   (wheel/double-click: zoom, drag: pan, F: fit)",
            hudFont, hudBrush, new PointF(8, Height - 24));
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        base.OnDoubleClick(e);
        var p = PointToClient(Cursor.Position);
        ZoomAt(p.X, p.Y, 3f);
    }

    private void ZoomAt(int sx, int sy, float factor)
    {
        var before = ScreenToWorld(sx, sy);
        _zoom = ClampF(_zoom * factor, 1e-6f, 50f);
        var after = ScreenToWorld(sx, sy);
        _center = new PointF(_center.X + before.X - after.X, _center.Y + before.Y - after.Y);
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ZoomAt(e.X, e.Y, e.Delta > 0 ? 1.25f : 0.8f);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            if (EditingEnabled && PickObject(e.Location) is int hit && hit >= 0)
            {
                var o = Objects[hit];
                var w = ScreenToWorld(e.X, e.Y);
                _dragging = true;
                _dragMoved = false;
                _dragIndex = hit;
                _dragOldX = o.X;
                _dragOldY = o.Y;
                _dragGrabOffset = new PointF(o.X - w.X, o.Y - w.Y);
                Cursor = Cursors.SizeAll;
                return;
            }
            _panning = true;
            _panStart = e.Location;
            _panStartCenter = _center;
        }
        else if (e.Button == MouseButtons.Right && _hover >= 0)
        {
            var o = Objects[_hover];
            var cm = new ContextMenuStrip();
            cm.Items.Add("Copy location (cm)", null, (s, ev) => Clipboard.SetText($"{o.X:0},{o.Y:0},{o.Z:0}"));
            cm.Items.Add("Copy location (m)", null, (s, ev) => Clipboard.SetText($"{o.X / 100:0.##},{o.Y / 100:0.##},{o.Z / 100:0.##}"));
            cm.Show(this, e.Location);
        }
    }

    /// <summary>Nearest object within pick radius (screen px), or -1.</summary>
    private int PickObject(Point p)
    {
        int best = -1;
        float bd = 14 * 14;
        for (int i = 0; i < Objects.Count; i++)
        {
            var o = Objects[i];
            if (!o.HasTransform) continue;
            var s = WorldToScreen(o.X, o.Y);
            float d = (s.X - p.X) * (s.X - p.X) + (s.Y - p.Y) * (s.Y - p.Y);
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
        {
            if (_dragging)
            {
                _dragging = false;
                Cursor = Cursors.Cross;
                if (_dragMoved)
                {
                    ObjectMoved?.Invoke(_dragIndex, _dragOldX, _dragOldY);
                }
                else
                {
                    // click without move on a grabbed object = select it
                    _selected = _dragIndex;
                    SelectionChanged?.Invoke(_dragIndex);
                    Invalidate();
                }
                return;
            }
            _panning = false;
            if (Math.Abs(e.X - _panStart.X) < 3 && Math.Abs(e.Y - _panStart.Y) < 3)
            {
                int best = PickObject(e.Location);
                _selected = best;
                SelectionChanged?.Invoke(best);
                Invalidate();
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            var w = ScreenToWorld(e.X, e.Y);
            var o = Objects[_dragIndex];
            var nx = w.X + _dragGrabOffset.X;
            var ny = w.Y + _dragGrabOffset.Y;
            if (MathF.Abs(nx - o.X) > 0.01f || MathF.Abs(ny - o.Y) > 0.01f)
            {
                o.X = nx;
                o.Y = ny;
                _dragMoved = true;
                _tip.SetToolTip(this, Describe(o) + "\n(release to drop, Ctrl+Z to undo)");
            }
            Invalidate();
            return;
        }
        if (_panning)
        {
            float dx = (_panStart.X - e.X) / _zoom;
            float dy = (e.Y - _panStart.Y) / _zoom;
            _center = new PointF(_panStartCenter.X + dx, _panStartCenter.Y + dy);
            _hover = -1;
            _tip.SetToolTip(this, "");
            Invalidate();
            return;
        }
        int hover = PickObject(e.Location);
        if (EditingEnabled)
            Cursor = hover >= 0 ? Cursors.SizeAll : Cursors.Cross;
        if (hover != _hover)
        {
            _hover = hover;
            _tip.SetToolTip(this, hover >= 0 ? Describe(Objects[hover]) : "");
        }
        Invalidate();
    }

    private static string Describe(MapObject o) =>
        $"{o.Name}\n{o.ClassName}\n{o.X / 100:0.#}, {o.Y / 100:0.#} m  (Z {o.Z / 100:0.#} m)";
}

