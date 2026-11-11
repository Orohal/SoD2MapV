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
        private readonly HashSet<int> _multi = new(); // additional selected indices (group selection)
        private int _hover = -1;
        private bool _panning;
        private Point _panStart;
        private PointF _panStartCenter;

        /// <summary>Current edit mode: Move (position), Rotate (yaw), Scale (uniform).</summary>
        public enum EditMode { Move, Rotate, Scale }

        /// <summary>Current edit mode.</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public EditMode Mode { get; set; } = EditMode.Move;

        /// <summary>Shift (cm) applied to the background basemaps: the basemaps live in true world
        /// coordinates, but tiled maps store their objects with tile-local transforms.
        /// world = local + offset, so basemaps are drawn at world − offset to match the canvas frame.
        /// Set from MapModel.WorldTileOffset* after loading a map.</summary>
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public float MapWorldOffsetX { get; set; }  // cm, default 0
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public float MapWorldOffsetY { get; set; }  // cm

        /// <summary>Region matching the currently loaded map (null = keep last / Trumbull).</summary>
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public MapRegion? MapRegion { get; set; }
        /// <summary>Loaded map path (used to pick the correct background asset variant).</summary>
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string? MapPath { get; set; }

        /// <summary>Draw the embedded fandom basemap (offline, no network) beneath the map graphics.</summary>
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool ShowFandomBase { get; set; } = true;

        /// <summary>Draw the vector basemap rendered from the game's own MapBackgroundData assets
        /// (StateOfDecay2/Content/maps/Backgrounds/*) — authentic and fully offline. Needs BgSource.</summary>
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool ShowBgBase { get; set; }

        /// <summary>When true, dragging pans the base-map layer (Bg) instead of the view, so its
        /// world offset can be tuned interactively. Commit happens on mouse-up (writes override file).</summary>
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool EditBgOffset { get; set; }

        /// <summary>Live drag offset (cm) applied to the base-map layer while EditBgOffset is active.</summary>
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public float BgNudgeX { get; set; }
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public float BgNudgeY { get; set; }
        /// <summary>Live drag offset (cm) applied to the fandom basemap while editing it.</summary>
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public float FandomNudgeX { get; set; }
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public float FandomNudgeY { get; set; }

        /// <summary>File provider used to load/render the vector basemap assets; null = disabled.</summary>
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public CUE4Parse.FileProvider.DefaultFileProvider? BgSource { get; set; }

        public event Action<int>? SelectionChanged; // index of selected object, -1 = none

        /// <summary>Radius (cm) used by "Select house group" around the clicked anchor object. Default 40 m.</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public float HouseSelectRadiusCm { get; set; } = 4000f;

        /// <summary>Indices currently selected (primary selection first). Read-only copy.</summary>
        public IReadOnlyList<int> Selection
        {
            get
            {
                if (_selected < 0 && _multi.Count == 0) return Array.Empty<int>();
                var list = new List<int>(_multi.Count + 1);
                if (_selected >= 0) list.Add(_selected);
                list.AddRange(_multi);
                return list;
            }
        }


        /// <summary>Optional player/character marker position in canvas-world cm (null = hidden).</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public PointF? PlayerWorldPos { get; set; }
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public float? PlayerWorldZCm { get; set; }
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string PlayerLabel { get; set; } = "Player";

        /// <summary>Fired after a drag moved an object: (index, oldX, oldY). Editing only.</summary>
        public event Action<int, float, float>? ObjectMoved;

        /// <summary>Fired after a group drag moved several objects together (Move mode;
        /// list of (index, oldX, oldY)). Editing only.</summary>
        public event Action<IReadOnlyList<(int Idx, float OldX, float OldY)>>? ObjectsMovedTogether;

        /// <summary>Fired after a drag rotated an object: (index, oldQX, oldQY, oldQZ, oldQW). Editing only.</summary>
        public event Action<int, float, float, float, float>? ObjectRotated;

        /// <summary>Fired after a drag scaled an object: (index, oldSX, oldSY, oldSZ). Editing only.</summary>
        public event Action<int, float, float, float>? ObjectScaled;

        /// <summary>Fired when the selected object is deleted: (index). Editing only.</summary>
        public event Action<int>? ObjectDeleted;

        /// <summary>Fired when the whole selection is deleted (list of indices). Editing only.</summary>
        public event Action<IReadOnlyList<int>>? ObjectsDeleted;

        /// <summary>When true, dragging an object edits it instead of panning the view.</summary>
        public bool EditingEnabled;

        private bool _dragging;
        private int _dragIndex = -1;
        private bool _dragMoved;
        private PointF _dragGrabOffset; // world offset mouse -> object at drag start
        private float _dragOldX, _dragOldY;

        // group drag state: all selected members move by the same delta in Move mode
        private readonly List<int> _dragMembers = new();
        private readonly List<(PointF Grab, float OldX, float OldY)> _dragMemberSnap = new();

        // rubber-band box selection state (Shift+Drag on empty space)
        private bool _boxSelecting;
        private Point _boxStart;
        private Point _boxEnd;
        private bool _boxAdditive;

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
        _multi.Clear();
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
        _multi.Clear();
        if (centerOnSelection && _selected >= 0 && _selected < Objects.Count)
        {
            var o = Objects[_selected];
            _center = new PointF(o.X, o.Y);
        }
        Invalidate();
    }

    /// <summary>Clears the whole selection (primary + group members).</summary>
    public void ClearSelection()
    {
        _selected = -1;
        _multi.Clear();
        SelectionChanged?.Invoke(-1);
        Invalidate();
    }

    /// <summary>Adds/removes a single object from the group selection (Ctrl+Click toggle).</summary>
    public void ToggleSelected(int index)
    {
        if (index < 0 || index >= Objects.Count) return;
        if (_multi.Contains(index)) _multi.Remove(index);
        else
        {
            _multi.Add(index);
            if (_selected < 0) _selected = index;
        }
        SelectionChanged?.Invoke(_selected);
        Invalidate();
    }

    /// <summary>Selects the anchor object plus every object within HouseSelectRadiusCm of it
    /// ("complete house" = house prefab + surrounding scatter objects), then zooms to fit
    /// the selected group ("autofit").</summary>
    public void SelectHouseGroup(int anchorIndex)
    {
        if (anchorIndex < 0 || anchorIndex >= Objects.Count) return;
        var a = Objects[anchorIndex];
        _selected = anchorIndex;
        _multi.Clear();
        float r2 = HouseSelectRadiusCm * HouseSelectRadiusCm;
        for (int i = 0; i < Objects.Count; i++)
        {
            if (i == anchorIndex) continue;
            var o = Objects[i];
            if (!o.HasTransform) continue;
            float dx = o.X - a.X, dy = o.Y - a.Y;
            if (dx * dx + dy * dy <= r2) _multi.Add(i);
        }
        SelectionChanged?.Invoke(anchorIndex);
        ZoomToSelection();
        _tip.SetToolTip(this, $"house group: {_multi.Count + 1} object(s) within {HouseSelectRadiusCm / 100:0} m of {a.Name}");
    }

    /// <summary>Zooms so the whole current selection fits in the view.</summary>
    public void ZoomToSelection()
    {
        var sel = Selection;
        if (sel.Count == 0) return;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var i in sel)
        {
            if (i < 0 || i >= Objects.Count) continue;
            var o = Objects[i];
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
        _zoom = MathF.Min(Width, Height) / span * 0.55f;
        if (!float.IsFinite(_zoom) || _zoom <= 0) _zoom = 0.01f;
        Invalidate();
    }

    /// <summary>Marks the selected object as deleted (flag only; canvas updates on next paint).</summary>
    public void DeleteSelected()
    {
        if (!EditingEnabled) return;
        DeleteSelection();
    }

    /// <summary>Deletes the whole selection (all group members or just the primary object).
    /// The canvas does NOT set flags itself — the caller (MainForm) records undo state and
    /// marks the objects, so undo can restore the pre-delete state.</summary>
    public void DeleteSelection()
    {
        if (!EditingEnabled) return;
        var sel = Selection;
        if (sel.Count == 0) return;
        if (sel.Count == 1)
            ObjectDeleted?.Invoke(sel[0]);
        else
            ObjectsDeleted?.Invoke(sel);
    }

    public void CenterOn(float worldX, float worldY)
    {
        _center = new PointF(worldX, worldY);
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

    /// <summary>Draws the Wand background: picks the zoom whose tile ≈ 256 px on screen,
    /// requests + draws every tile intersecting the viewport. Coarse level first, fine level
    /// on top — so loaded fine tiles cover the coarse filler (gap-free pyramid look).</summary>
    /// <summary>Draws the embedded fandom blank-map image at its calibrated world rect —
    /// purely offline (image is embedded in the EXE). Serves as the complete base layer
    /// beneath the (often hole-ridden) WeMod tile pyramid.</summary>
    /// <summary>Vector basemap drawn straight from the game's MapBackgroundData assets.
    /// Rendered lazily on first paint of a region (few seconds), cached per provider+region.</summary>
    private void DrawBgBasemap(Graphics g)
    {
        if (BgSource == null) return;
        try
        {
            var region = MapRegion ?? MapRegions.TrumbullValley;
            var bm = BgMaps.Get(BgSource, region, 1800, MapPath);
            if (bm == null) return;
            var pTL = WorldToScreen(bm.WorldX0 + BgNudgeX - MapWorldOffsetX, bm.WorldY0 + BgNudgeY - MapWorldOffsetY);
            var w = (bm.WorldX1 - bm.WorldX0) * _zoom;
            var h = (bm.WorldY1 - bm.WorldY0) * _zoom;
            if (pTL.X + w < -50 || pTL.X > Width + 50 || pTL.Y + h < -50 || pTL.Y > Height + 50) return;
            var prevInterp = g.InterpolationMode;
            if (w > 3 * bm.Bitmap.Width)
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.DrawImage(bm.Bitmap, new RectangleF(pTL.X, pTL.Y, w, h));
            }
            else
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                g.DrawImage(bm.Bitmap, pTL.X, pTL.Y, w, h);
            }
            g.InterpolationMode = prevInterp;
        }
        catch (Exception ex)
        {
            using var f = new Font("Segoe UI", 8f);
            g.DrawString("Vector basemap error: " + ex.Message, f, Brushes.OrangeRed, 8, 32);
        }
    }

    private void DrawFandomBasemap(Graphics g)
    {
        try
        {
            var region = MapRegion ?? MapRegions.TrumbullValley;
            var map = FandomMaps.For(region);
            if (map == null) return;
            var bmp = FandomMaps.GetBitmap(map);
            if (bmp == null) return;
            // fandom rect is in world cm; canvas frame is local (= world − offset)
            var pTL = WorldToScreen(map.WorldX0 + FandomNudgeX - MapWorldOffsetX, map.WorldY0 + FandomNudgeY - MapWorldOffsetY);
            var w = (map.WorldX1 - map.WorldX0) * _zoom;
            var h = (map.WorldY1 - map.WorldY0) * _zoom;
            if (pTL.X + w < -50 || pTL.X > Width + 50 || pTL.Y + h < -50 || pTL.Y > Height + 50) return;
            var prevInterp = g.InterpolationMode;
            if (w > 3 * bmp.Width)
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.DrawImage(bmp, new RectangleF(pTL.X, pTL.Y, w, h));
            }
            else
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                g.DrawImage(bmp, pTL.X, pTL.Y, w, h);
            }
            g.InterpolationMode = prevInterp;
        }
        catch (Exception ex)
        {
            using var f = new Font("Segoe UI", 8f);
            g.DrawString("Fandom basemap error: " + ex.Message, f, Brushes.OrangeRed, 8, 20);
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

        // background: fandom (stylized) underneath, then vector asset basemap (accurate) on top
        if (ShowFandomBase)
            DrawFandomBasemap(g);
        if (ShowBgBase)
            DrawBgBasemap(g);

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
            if (i == _hover || i == _selected || _multi.Contains(i))
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
            if (EditingEnabled && !o.Deleted && (i == _selected || (_multi.Contains(i) && _multi.Count > 0)))
            {
                if (i == _selected)
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
                else
                {
                    // group member: smaller orange dashed ring
                    var selSize = (int)Math.Max(dot * 1.5f + 3f, 6f);
                    using var hl2 = new Pen(Color.OrangeRed, 1.5f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
                    g.DrawEllipse(hl2, p.X - selSize, p.Y - selSize, selSize * 2, selSize * 2);
                }
            }
        }

        // dashed bounding box around the whole group selection
        if (_multi.Count > 1)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            var ok = false;
            foreach (var i in _multi)
            {
                if (i < 0 || i >= Objects.Count) continue;
                var mo = Objects[i];
                if (!mo.HasTransform) continue;
                var sp = WorldToScreen(mo.X, mo.Y);
                minX = MathF.Min(minX, sp.X); minY = MathF.Min(minY, sp.Y);
                maxX = MathF.Max(maxX, sp.X); maxY = MathF.Max(maxY, sp.Y);
                ok = true;
            }
            if (ok)
            {
                minX -= 12; minY -= 12; maxX += 12; maxY += 12;
                using var grpPen = new Pen(Color.FromArgb(255, 140, 80), 1.5f)
                    { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
                g.DrawRectangle(grpPen, minX, minY, maxX - minX, maxY - minY);
            }
        }

        // rubber-band box selection overlay
        if (_boxSelecting)
        {
            var r = BoxRect(_boxStart, _boxEnd);
            using var boxPen = new Pen(Color.White, 1f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            using var boxFill = new SolidBrush(Color.FromArgb(30, 255, 255, 255));
            g.FillRectangle(boxFill, r);
            g.DrawRectangle(boxPen, r.X, r.Y, r.Width, r.Height);
        }

        // player/character marker (on top)
        if (PlayerWorldPos is { } pw)
        {
            var pp = WorldToScreen(pw.X, pw.Y);
            float r = Math.Max(7f, 10f * _zoom < 1f ? 8f : Math.Min(14f, 8f + _zoom * 200f));
            using var fill = new SolidBrush(Color.FromArgb(230, 60, 220, 255));
            using var pen = new Pen(Color.White, 2f);
            using var pen2 = new Pen(Color.FromArgb(200, 0, 40, 60), 3f);
            g.FillEllipse(fill, pp.X - r, pp.Y - r, r * 2, r * 2);
            g.DrawEllipse(pen2, pp.X - r, pp.Y - r, r * 2, r * 2);
            g.DrawEllipse(pen, pp.X - r, pp.Y - r, r * 2, r * 2);
            g.DrawLine(pen, pp.X - r - 4, pp.Y, pp.X + r + 4, pp.Y);
            g.DrawLine(pen, pp.X, pp.Y - r - 4, pp.X, pp.Y + r + 4);
            using var lf = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            var zPart = PlayerWorldZCm is float zcm ? $", Z {zcm / 100:0.#}" : "";
            var label = $"{PlayerLabel} ({pw.X / 100:0.#}, {pw.Y / 100:0.#}{zPart} m)";
            var sz = g.MeasureString(label, lf);
            var lx = pp.X + r + 5; var ly = pp.Y - sz.Height / 2;
            using var bg = new SolidBrush(Color.FromArgb(180, 20, 24, 30));
            g.FillRectangle(bg, lx - 2, ly - 1, sz.Width + 4, sz.Height + 2);
            g.DrawString(label, lf, Brushes.White, lx, ly);
        }

        using var hudFont = new Font("Segoe UI", 8.5f);
        using var hudBrush = new SolidBrush(Color.FromArgb(150, 200, 210));
        g.DrawString(
            $"zoom {_zoom:0.000} px/cm   center [{_center.X / 100:0.#} m / {_center.Y / 100:0.#} m]   mode={Mode}   sel={Selection.Count}   hover: {(_hover >= 0 ? Objects[_hover].Name : "-")}   (wheel/dbl-click: zoom, drag: pan, F: fit, Ctrl+click: multi-select, Shift+drag: box, right-click: select house group, Esc: clear)",
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

    private bool _bgPanning, _fandomPanning;
    private float _bgNudgeStartX, _bgNudgeStartY, _fandomNudgeStartX, _fandomNudgeStartY;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && EditBgOffset)
        {
            bool editFandom = ShowFandomBase && !ShowBgBase;
            if (editFandom) _fandomPanning = true; else _bgPanning = true;
            _panStart = e.Location;
            if (editFandom) { _fandomNudgeStartX = FandomNudgeX; _fandomNudgeStartY = FandomNudgeY; }
            else { _bgNudgeStartX = BgNudgeX; _bgNudgeStartY = BgNudgeY; }
            Cursor = Cursors.SizeAll;
            return;
        }
        if (e.Button == MouseButtons.Left)
        {
            bool ctrl = (ModifierKeys & Keys.Control) != 0;
            bool shift = (ModifierKeys & Keys.Shift) != 0;
            if (EditingEnabled && PickObject(e.Location) is int hit && hit >= 0)
            {
                if (ctrl && !shift)
                {
                    // Ctrl+Click = toggle group membership (no drag)
                    ToggleSelected(hit);
                    return;
                }
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

                // snapshot group members (Move mode): everyone moves together
                _dragMembers.Clear();
                _dragMemberSnap.Clear();
                if (Mode == EditMode.Move)
                {
                    var grab = new PointF(o.X - w.X, o.Y - w.Y);
                    _dragMembers.Add(hit);
                    _dragMemberSnap.Add((grab, o.X, o.Y));
                    foreach (var m in _multi)
                    {
                        if (m == hit) continue;
                        var mo = Objects[m];
                        _dragMembers.Add(m);
                        _dragMemberSnap.Add((new PointF(mo.X - w.X, mo.Y - w.Y), mo.X, mo.Y));
                    }
                }

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
            if (shift)
            {
                // Shift+Drag on empty space = rubber-band box selection
                _boxSelecting = true;
                _boxAdditive = ctrl;
                _boxStart = e.Location;
                _boxEnd = e.Location;
                Cursor = Cursors.Cross;
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
            cm.Items.Add($"Select house group ({HouseSelectRadiusCm / 100:0} m around object)", null,
                (s, ev) => SelectHouseGroup(_hover));
            if (_multi.Count > 0)
            {
                cm.Items.Add("Zoom to selection", null, (s, ev) => ZoomToSelection());
                cm.Items.Add("Clear selection (Esc)", null, (s, ev) => ClearSelection());
            }
            cm.Items.Add("-");
            cm.Items.Add(Selection.Count > 1 ? $"Delete selection ({Selection.Count}, Del)" : "Delete object (Del)",
                null, (s, ev) => DeleteSelection());
            cm.Show(this, e.Location);
        }
    }

    private static RectangleF BoxRect(Point a, Point b)
    {
        var x = MathF.Min(a.X, b.X);
        var y = MathF.Min(a.Y, b.Y);
        return new RectangleF(x, y, MathF.Abs(a.X - b.X), MathF.Abs(a.Y - b.Y));
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
                    if (Mode == EditMode.Move && _dragMembers.Count > 1)
                    {
                        // group drag: fire one combined event so MainForm pushes a single undo entry
                        var moves = new List<(int Idx, float OldX, float OldY)>(_dragMembers.Count);
                        for (var k = 0; k < _dragMembers.Count; k++)
                            moves.Add((_dragMembers[k], _dragMemberSnap[k].OldX, _dragMemberSnap[k].OldY));
                        ObjectsMovedTogether?.Invoke(moves);
                    }
                    else if (Mode == EditMode.Move)
                        ObjectMoved?.Invoke(_dragIndex, _dragOldX, _dragOldY);
                    else if (Mode == EditMode.Rotate)
                        ObjectRotated?.Invoke(_dragIndex, _dragOldQX, _dragOldQY, _dragOldQZ, _dragOldQW);
                    else if (Mode == EditMode.Scale)
                        ObjectScaled?.Invoke(_dragIndex, _dragOldSX, _dragOldSY, _dragOldSZ);
                }
                else
                {
                    // click without move on a grabbed object = select it (single)
                    _selected = _dragIndex;
                    _multi.Clear();
                    SelectionChanged?.Invoke(_dragIndex);
                    Invalidate();
                }
                _dragMembers.Clear();
                _dragMemberSnap.Clear();
                return;
            }
            if (_boxSelecting)
            {
                _boxSelecting = false;
                Cursor = Cursors.Cross;
                var r = BoxRect(_boxStart, e.Location);
                if (r.Width < 5 && r.Height < 5)
                {
                    // tiny box = plain click
                    var best = PickObject(e.Location);
                    if ((ModifierKeys & Keys.Control) != 0 && best >= 0) ToggleSelected(best);
                    else { _selected = best; _multi.Clear(); SelectionChanged?.Invoke(best); }
                    Invalidate();
                    return;
                }
                if (!_boxAdditive) _multi.Clear();
                var count = 0;
                for (int i = 0; i < Objects.Count; i++)
                {
                    var o = Objects[i];
                    if (!o.HasTransform) continue;
                    var s = WorldToScreen(o.X, o.Y);
                    if (r.Contains(s)) { _multi.Add(i); count++; }
                }
                _selected = _multi.Count > 0 ? _multi.Min() : _selected;
                // drop primary duplicate
                _multi.Remove(_selected);
                SelectionChanged?.Invoke(_selected);
                _tip.SetToolTip(this, $"selected {count} object(s) by box");
                Invalidate();
                return;
            }
            if (_bgPanning)
            {
                _bgPanning = false;
                Cursor = Cursors.Cross;
                var region = MapRegion ?? MapRegions.TrumbullValley;
                var asset = BgMaps.AssetFor(region, MapPath);
                var cur = BgCalib.Get(region.Key, asset);
                double newOx = cur.OX + BgNudgeX;
                double newOy = cur.OY + BgNudgeY;
                BgCalib.SetOverride(region.Key, cur.SX, cur.SY, newOx, newOy, asset);
                BgMaps.ClearCache();
                BgNudgeX = 0; BgNudgeY = 0;
                _tip.SetToolTip(this, $"committed: ox={newOx:0} oy={newOy:0} cm");
                Invalidate();
                return;
            }
            if (_fandomPanning)
            {
                _fandomPanning = false;
                Cursor = Cursors.Cross;
                var region = MapRegion ?? MapRegions.TrumbullValley;
                var cur = FandomMaps.GetOffset(region.Key);
                double ndx = cur.DX + FandomNudgeX;
                double ndy = cur.DY + FandomNudgeY;
                FandomMaps.SetOffset(region.Key, ndx, ndy);
                FandomNudgeX = 0; FandomNudgeY = 0;
                _tip.SetToolTip(this, $"Fandom map committed: dx={ndx:0} dy={ndy:0} cm");
                Invalidate();
                return;
            }
            _panning = false;
            if (Math.Abs(e.X - _panStart.X) < 3 && Math.Abs(e.Y - _panStart.Y) < 3)
            {
                int best = PickObject(e.Location);
                if ((ModifierKeys & Keys.Control) != 0 && EditingEnabled && best >= 0)
                    ToggleSelected(best);
                else
                {
                    _selected = best;
                    _multi.Clear();
                    SelectionChanged?.Invoke(best);
                }
                Invalidate();
            }
        }
        else if (e.Button == MouseButtons.Right)
        {
            // world position inject (MainForm opens an Inject dialog) only on empty map background;
            // when an object is under the cursor the object/house context menu wins instead
            if (_hover < 0 && PickObject(e.Location) < 0)
            {
                var w = ScreenToWorld(e.X, e.Y);
                WorldRightClicked?.Invoke(w);
            }
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
                    // move the whole group by the same delta
                    if (_dragMembers.Count > 1)
                    {
                        for (var k = 1; k < _dragMembers.Count; k++)
                        {
                            var mo = Objects[_dragMembers[k]];
                            mo.X = w.X + _dragMemberSnap[k].Grab.X;
                            mo.Y = w.Y + _dragMemberSnap[k].Grab.Y;
                        }
                    }
                    _tip.SetToolTip(this, Describe(o) + (_dragMembers.Count > 1 ? $"  [{_dragMembers.Count} objects]" : "")
                        + "\n(release to drop, Ctrl+Z to undo)");
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
        if (_bgPanning)
        {
            BgNudgeX = _bgNudgeStartX + (e.X - _panStart.X) / _zoom;
            BgNudgeY = _bgNudgeStartY + (e.Y - _panStart.Y) / _zoom;
            _tip.SetToolTip(this, $"vector map nudge: X={BgNudgeX:0} Y={BgNudgeY:0} cm  (release to commit)");
            Invalidate();
            return;
        }
        if (_fandomPanning)
        {
            FandomNudgeX = _fandomNudgeStartX + (e.X - _panStart.X) / _zoom;
            FandomNudgeY = _fandomNudgeStartY + (e.Y - _panStart.Y) / _zoom;
            _tip.SetToolTip(this, $"Fandom map nudge: X={FandomNudgeX:0} Y={FandomNudgeY:0} cm  (release to commit)");
            Invalidate();
            return;
        }
        if (_boxSelecting)
        {
            _boxEnd = e.Location;
            _tip.SetToolTip(this, "release to select all objects in the box" + (_boxAdditive ? " (adding to selection)" : ""));
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

