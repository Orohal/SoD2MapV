using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using CUE4Parse.UE4.Objects.Core.Math;

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

        /// <summary>Current edit mode: Move (position), Rotate (yaw), Scale (uniform).</summary>
        public enum EditMode { Move, Rotate, Scale }

        /// <summary>Current edit mode.</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public EditMode Mode { get; set; } = EditMode.Move;

        /// <summary>Optional background layer with the real in-game map (Wand tiles); null = hidden.</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public WandTileLayer? WandLayer
        {
            get => _wandLayer;
            set
            {
                if (_wandLayer != null) _wandLayer.Updated -= OnWandTileLoaded;
                _wandLayer = value;
                if (_wandLayer != null) _wandLayer.Updated += OnWandTileLoaded;
                Invalidate();
            }
        }
        private WandTileLayer? _wandLayer;

        /// <summary>Shift (cm) applied to Wand tiles: the background tiles live in true world
        /// coordinates, but tiled maps store their objects with tile-local transforms.
        /// world = local + offset, so tiles are drawn at world − offset to match the canvas frame.
        /// Set from MapModel.WorldTileOffset* after loading a map.</summary>
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public float WandWorldOffsetX { get; set; }  // cm, default 0
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public float WandWorldOffsetY { get; set; }  // cm

        private void OnWandTileLoaded()
        {
            try { if (IsHandleCreated) BeginInvoke(() => Invalidate()); } catch (ObjectDisposedException) { }
        }

        public event Action<int>? SelectionChanged; // index of selected object, -1 = none

        /// <summary>Fired after a drag moved an object: (index, oldX, oldY). Editing only.</summary>
        public event Action<int, float, float>? ObjectMoved;

        /// <summary>Fired after a drag rotated an object: (index, oldQX, oldQY, oldQZ, oldQW). Editing only.</summary>
        public event Action<int, float, float, float, float>? ObjectRotated;

        /// <summary>Fired after a drag scaled an object: (index, oldSX, oldSY, oldSZ). Editing only.</summary>
        public event Action<int, float, float, float>? ObjectScaled;

        /// <summary>Fired when the selected object is deleted: (index). Editing only.</summary>
        public event Action<int>? ObjectDeleted;

        /// <summary>When true, dragging an object edits it instead of panning the view.</summary>
        public bool EditingEnabled;

        private bool _dragging;
        private int _dragIndex = -1;
        private bool _dragMoved;
        private PointF _dragGrabOffset; // world offset mouse -> object at drag start
        private float _dragOldX, _dragOldY;

        // rotate / scale drag state
        private float _dragOldQX, _dragOldQY, _dragOldQZ, _dragOldQW;
        private float _dragOldSX, _dragOldSY, _dragOldSZ;
        private Point _dragStartScreen;
        private float _dragStartYaw;
        private float _dragStartDist;

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

    /// <summary>Marks the selected object as deleted (flag only; canvas updates on next paint).</summary>
    public void DeleteSelected()
    {
        if (!EditingEnabled) return;
        if (_selected < 0 || _selected >= Objects.Count) return;
        Objects[_selected].Deleted = true;
        Invalidate();
        ObjectDeleted?.Invoke(_selected);
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

    /// <summary>Draws the Wand background: picks the zoom whose tile ≈ 256 px on screen,
    /// requests + draws every tile intersecting the viewport (z and z−1 for gap-free loading).</summary>
    private void DrawWandTiles(Graphics g)
    {
        try
        {
            var visibleMin = ScreenToWorld(0, Height);
            var visibleMax = ScreenToWorld(Width, 0);

            // nearest tile zoom for the current on-screen scale
            var z = WandTiles.MaxZoom;
            while (z > WandTiles.MinZoom)
            {
                var probe = WandTiles.TileWorldRect(z, 0, 0);
                if ((probe.X1 - probe.X0) * _zoom >= 256) break;
                z--;
            }

            for (var level = Math.Min(WandTiles.MaxZoom, z + 1); level >= z; level--)
                DrawWandLevel(g, level, visibleMin, visibleMax);
        }
        catch (Exception ex)
        {
            using var f = new Font("Segoe UI", 8f);
            g.DrawString("Wand layer error: " + ex.Message, f, Brushes.OrangeRed, 8, 8);
        }
    }

    private void DrawWandLevel(Graphics g, int z, PointF visibleMin, PointF visibleMax)
    {
        const float maxLatDeg = 85.0f;
        var corners = new[] { visibleMin, visibleMax, ScreenToWorld(0, 0), ScreenToWorld(Width, Height) };
        int txMin = int.MaxValue, txMax = int.MinValue, tyMin = int.MaxValue, tyMax = int.MinValue;
        foreach (var c in corners)
        {
            // the tile request works in true world space; the canvas frame is local (= world − offset)
            var (lng, lat) = WandTiles.LngLatFromWorld(c.X + WandWorldOffsetX, c.Y + WandWorldOffsetY);
            var latF = ClampF((float)lat, -maxLatDeg, maxLatDeg);
            var n = Math.Pow(2.0, z);
            var tx = (int)Math.Floor((lng + 180.0) / 360.0 * n);
            var ty = (int)Math.Floor((1.0 - Math.Log(Math.Tan(Math.PI / 4.0 + latF * Math.PI / 360.0)) / Math.PI) / 2.0 * n);
            txMin = Math.Min(txMin, tx); txMax = Math.Max(txMax, tx);
            tyMin = Math.Min(tyMin, ty); tyMax = Math.Max(tyMax, ty);
        }
        txMin = Math.Max(txMin, 0); tyMin = Math.Max(tyMin, 0);
        var pow = (int)Math.Pow(2.0, z) - 1;
        txMax = Math.Min(txMax, pow); tyMax = Math.Min(tyMax, pow);

        for (var tx = txMin; tx <= txMax; tx++)
        for (var ty = tyMin; ty <= tyMax; ty++)
        {
            var tile = WandLayer!.Peek(z, tx, ty);
            if (tile == null)
            {
                WandLayer.EnsureLoaded(z, tx, ty);
                continue;
            }
            var pTL = WorldToScreen(tile.X0 - WandWorldOffsetX, tile.Y0 - WandWorldOffsetY);
            var w = (tile.X1 - tile.X0) * _zoom;
            var h = (tile.Y1 - tile.Y0) * _zoom;
            if (pTL.X + w < -50 || pTL.Y + h < -50 || pTL.X > Width + 50 || pTL.Y > Height + 50) continue;
            var prevInterp = g.InterpolationMode;
            g.InterpolationMode = w > 3 * tile.Bitmap.Width
                ? System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor
                : System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.DrawImage(tile.Bitmap, pTL.X, pTL.Y, w, h);
            g.InterpolationMode = prevInterp;
        }
    }

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

        // background: real map tiles (Wand)
        if (WandLayer != null)
            DrawWandTiles(g);

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
            if (o.Deleted)
                col = Color.FromArgb(120, 120, 120); // grayed out for deleted
            var b = new SolidBrush(col);
            var size = dot;
            if (i == _selected) size = dot * 2f + 4f;
            else if (i == _hover) size = dot * 1.5f + 2f;
            using (b)
                g.FillRectangle(b, p.X - size / 2, p.Y - size / 2, size, size);

            if (o.Deleted)
            {
                using var strike = new Pen(Color.Red, 2f);
                g.DrawLine(strike, p.X - size / 2 - 2, p.Y, p.X + size / 2 + 2, p.Y);
                g.DrawLine(strike, p.X - 2, p.Y - size / 2 - 2, p.X + 2, p.Y + size / 2 + 2);
            }

            // selection highlight + edit-mode visualizations
            if (i == _selected && EditingEnabled && !o.Deleted)
            {
                using var hl = new Pen(Color.White, 2f);
                g.DrawEllipse(hl, p.X - size, p.Y - size, size * 2, size * 2);

                if (Mode == EditMode.Rotate)
                {
                    // yaw arrow pointing in the object's forward direction
                    float yaw = YawFromQuat(o.QX, o.QY, o.QZ, o.QW);
                    float arrowLen = MathF.Max(dot * 3f, 12f);
                    var end = new PointF(p.X + MathF.Cos(yaw) * arrowLen, p.Y - MathF.Sin(yaw) * arrowLen);
                    using var arr = new Pen(Color.Yellow, 2f);
                    g.DrawLine(arr, p.X, p.Y, end.X, end.Y);
                    g.FillEllipse(new SolidBrush(Color.Yellow), end.X - 3, end.Y - 3, 6, 6);
                }
                else if (Mode == EditMode.Scale)
                {
                    // scale indicator: square around the object sized by average scale
                    float avgScale = (o.SX + o.SY + o.SZ) / 3f;
                    float s = MathF.Max(dot * avgScale * 1.5f, 8f);
                    using var sq = new Pen(Color.Cyan, 1.5f);
                    g.DrawRectangle(sq, p.X - s / 2, p.Y - s / 2, s, s);
                }
            }
        }

        using var hudFont = new Font("Segoe UI", 8.5f);
        using var hudBrush = new SolidBrush(Color.FromArgb(150, 200, 210));
        g.DrawString(
            $"zoom {_zoom:0.000} px/cm   center [{_center.X / 100:0.#} m / {_center.Y / 100:0.#} m]   mode={Mode}   hover: {(_hover >= 0 ? Objects[_hover].Name : "-")}   (wheel/double-click: zoom, drag: pan, F: fit)",
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
                _dragStartScreen = e.Location;
                _dragGrabOffset = new PointF(o.X - w.X, o.Y - w.Y);
                _dragOldX = o.X;
                _dragOldY = o.Y;
                _dragOldQX = o.QX; _dragOldQY = o.QY; _dragOldQZ = o.QZ; _dragOldQW = o.QW;
                _dragOldSX = o.SX; _dragOldSY = o.SY; _dragOldSZ = o.SZ;

                if (Mode == EditMode.Rotate)
                {
                    _dragStartYaw = YawFromQuat(o.QX, o.QY, o.QZ, o.QW);
                }
                else if (Mode == EditMode.Scale)
                {
                    var selPos = WorldToScreen(o.X, o.Y);
                    _dragStartDist = (float)MathF.Sqrt((e.X - selPos.X) * (e.X - selPos.X) + (e.Y - selPos.Y) * (e.Y - selPos.Y));
                }

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
            cm.Items.Add("-");
            cm.Items.Add("Mode: Move (M)", null, (s, ev) => { Mode = EditMode.Move; Invalidate(); });
            cm.Items.Add("Mode: Rotate (R)", null, (s, ev) => { Mode = EditMode.Rotate; Invalidate(); });
            cm.Items.Add("Mode: Scale (S)", null, (s, ev) => { Mode = EditMode.Scale; Invalidate(); });
            cm.Items.Add("-");
            cm.Items.Add("Delete object (Del)", null, (s, ev) => DeleteSelected());
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
                    var o = Objects[_dragIndex];
                    if (Mode == EditMode.Move)
                        ObjectMoved?.Invoke(_dragIndex, _dragOldX, _dragOldY);
                    else if (Mode == EditMode.Rotate)
                        ObjectRotated?.Invoke(_dragIndex, _dragOldQX, _dragOldQY, _dragOldQZ, _dragOldQW);
                    else if (Mode == EditMode.Scale)
                        ObjectScaled?.Invoke(_dragIndex, _dragOldSX, _dragOldSY, _dragOldSZ);
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
        else if (e.Button == MouseButtons.Right)
        {
            var w = ScreenToWorld(e.X, e.Y);
            WorldRightClicked?.Invoke(w);
        }
    }

    /// <summary>Fired on right-click with the world position (UE cm) under the cursor.</summary>
    public event Action<PointF>? WorldRightClicked;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            var w = ScreenToWorld(e.X, e.Y);
            var o = Objects[_dragIndex];

            if (Mode == EditMode.Move)
            {
                var nx = w.X + _dragGrabOffset.X;
                var ny = w.Y + _dragGrabOffset.Y;
                if (MathF.Abs(nx - o.X) > 0.01f || MathF.Abs(ny - o.Y) > 0.01f)
                {
                    o.X = nx;
                    o.Y = ny;
                    _dragMoved = true;
                    _tip.SetToolTip(this, Describe(o) + "\n(release to drop, Ctrl+Z to undo)");
                }
            }
            else if (Mode == EditMode.Rotate)
            {
                float dx = e.X - _dragStartScreen.X;
                float dy = e.Y - _dragStartScreen.Y;
                float sensitivity = 0.005f; // rad per pixel
                float newYaw = _dragStartYaw + dx * sensitivity - dy * sensitivity;
                var q = QuatFromYaw(newYaw);
                if (MathF.Abs(q.X - o.QX) > 0.0001f || MathF.Abs(q.Y - o.QY) > 0.0001f ||
                    MathF.Abs(q.Z - o.QZ) > 0.0001f || MathF.Abs(q.W - o.QW) > 0.0001f)
                {
                    o.QX = q.X; o.QY = q.Y; o.QZ = q.Z; o.QW = q.W;
                    _dragMoved = true;
                    _tip.SetToolTip(this, Describe(o) + $"\nyaw={newYaw * 180f / MathF.PI:0.#}°  (release to drop, Ctrl+Z to undo)");
                }
            }
            else if (Mode == EditMode.Scale)
            {
                var selPos = WorldToScreen(o.X, o.Y);
                float curDist = (float)MathF.Sqrt((e.X - selPos.X) * (e.X - selPos.X) + (e.Y - selPos.Y) * (e.Y - selPos.Y));
                float factor = curDist / MathF.Max(_dragStartDist, 1f);
                float nsx = _dragOldSX * factor;
                float nsy = _dragOldSY * factor;
                float nsz = _dragOldSZ * factor;
                if (MathF.Abs(nsx - o.SX) > 0.001f || MathF.Abs(nsy - o.SY) > 0.001f || MathF.Abs(nsz - o.SZ) > 0.001f)
                {
                    o.SX = nsx; o.SY = nsy; o.SZ = nsz;
                    _dragMoved = true;
                    _tip.SetToolTip(this, Describe(o) + $"\nscale={o.SX:0.##}  (release to drop, Ctrl+Z to undo)");
                }
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

    /// <summary>Extract yaw (Z-rotation) from a quaternion.</summary>
    private static float YawFromQuat(float x, float y, float z, float w)
        => MathF.Atan2(2f * (w * y + x * z), 1f - 2f * (y * y + z * z));

    /// <summary>Create a quaternion from a pure yaw angle (Z-rotation).</summary>
    private static FQuat QuatFromYaw(float yaw)
    {
        float h = yaw * 0.5f;
        return new FQuat(0f, 0f, MathF.Sin(h), MathF.Cos(h));
    }
}

