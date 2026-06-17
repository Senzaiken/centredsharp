using System.Diagnostics.CodeAnalysis;
using CentrED.Blueprints;
using CentrED.Client;
using CentrED.Lights;
using CentrED.Network;
using CentrED.Renderer;
using CentrED.Renderer.Effects;
using CentrED.Tools;
using CentrED.UI.Windows;
using ClassicUO.Assets;
using ClassicUO.IO;
using ClassicUO.Renderer.Arts;
using ClassicUO.Renderer.Texmaps;
using ClassicUO.Utility;
using FontStashSharp;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using static CentrED.Application;
using static CentrED.Constants;
using Color = System.Drawing.Color;
using FNARectangle = Microsoft.Xna.Framework.Rectangle;
using FNAColor = Microsoft.Xna.Framework.Color;
using FNAVector2 = Microsoft.Xna.Framework.Vector2;
using FNAVector3 = Microsoft.Xna.Framework.Vector3;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace CentrED.Map;

public class MapManager
{
    private readonly GraphicsDevice _gfxDevice;
    private readonly GameWindow _gameWindow;
    private readonly MapRenderer _mapRenderer;
    private readonly SpriteBatch _spriteBatch;
    private readonly Texture2D _background;
    private readonly FontSystem _fontSystem;
    private readonly Keymap _keymap;

    private RenderTarget2D _selectionBuffer;
    public bool DebugDrawSelectionBuffer;
    private RenderTarget2D _lightMap;
    public bool DebugDrawLightMap;
    private RenderTarget2D _worldRenderTarget;
    public RenderTarget2D Output => _worldRenderTarget;
    public int ViewMouseX, ViewMouseY;
    public bool ViewHovered, ViewFocused;
    public bool WindowVisible;
    private int _prevMouseX, _prevMouseY;
    
    public MapEffect MapEffect { get; private set; }

    private static UOFileManager _assets;
    private static AnimatedStaticsManager _animatedStaticsManager;
    private static Art _sharedArts;
    private static Texmap _sharedTexmaps;
    private static BlueprintManager _sharedBlueprints;
    private static string _loadedClientPath = "";
    private static TileDataLand[] _sharedLandTileData = [];
    private static TileDataStatic[] _sharedStaticTileData = [];
    public UOFileManager UoFileManager => _assets;
    public Art Arts => _sharedArts;
    public Texmap Texmaps => _sharedTexmaps;
    public BlueprintManager BlueprintManager => _sharedBlueprints;

    private static readonly List<Tool> _sharedTools = [];
    internal List<Tool> Tools => _sharedTools;
    private static Tool _activeTool;

    public Tool ActiveTool
    {
        get => _activeTool;
        set
        {
            _activeTool?.OnMouseLeave(Selected);
            _activeTool?.OnDeactivated(Selected);
            _activeTool = value;
            _activeTool.OnActivated(Selected);
            _activeTool.OnMouseEnter(Selected);
        }
    }
    
    private readonly CentrEDClient Client;

    public bool ShowLand = true;
    public bool ShowStatics = true;
    public bool ShowVirtualLayer = false;
    public bool ShowNoDraw = false;
    public int VirtualLayerZ;
    public bool UseVirtualLayer = false;
    public bool WalkableSurfaces = false;
    public bool FlatView = false;
    public bool FlatShowHeight = false;
    public bool AnimatedStatics = true;
    public bool ShowGrid = false;
    public bool DebugLogging;
    public bool DebugInvalidTiles;

    public readonly Camera Camera = new();

    private DepthStencilState _DepthStencilState = new()
    {
        DepthBufferEnable = true,
        DepthBufferWriteEnable = true,
        DepthBufferFunction = CompareFunction.Less,
        StencilEnable = false
    };

    public int MinZ = -128;
    public int MaxZ = 127;

    public SortedSet<int> ObjectIdFilter = new();
    public bool ObjectIdFilterEnabled;
    public bool ObjectIdFilterInclusive = true;
    public SortedSet<int> ObjectHueFilter = new();
    public bool ObjectHueFilterEnabled;
    public bool ObjectHueFilterInclusive = true;
    public HashSet<LandObject> _ToRecalculate = new();

    private static readonly List<ushort> _sharedValidLandIds = [];
    private static readonly List<ushort> _sharedValidStaticIds = [];
    public List<ushort> ValidLandIds => _sharedValidLandIds;
    public List<ushort> ValidStaticIds => _sharedValidStaticIds;

    public MapManager(GraphicsDevice gd, GameWindow window, Keymap keymap, CentrEDClient client)
    {
        _gfxDevice = gd;
        _gameWindow = window;
        _keymap = keymap;
        StaticsManager.MapManager = this;

        MapEffect = new MapEffect(gd);
        _mapRenderer = new MapRenderer(gd, window);
        _spriteBatch = new SpriteBatch(gd);
        _fontSystem = new FontSystem();
        
        _fontSystem.AddFont(File.ReadAllBytes("roboto.ttf"));
        
        using (var fileStream = File.OpenRead("background.png"))
        {
            _background = Texture2D.FromStream(gd, fileStream);
        }
        
        Client = client;
        Client.Connected += OnConnected;
        Client.Disconnected += OnDisconnected;
        EnableBlockLoading();
        Client.LandTileReplaced += OnLandTileReplaced;
        Client.LandTileElevated += OnLandTileElevated;
        Client.StaticTileAdded += StaticsManager.Add;
        Client.StaticTileRemoved += StaticsManager.Remove;
        Client.StaticTileMoved += StaticsManager.Move;
        Client.StaticTileElevated += StaticsManager.Elevate;
        Client.StaticTileHued += HueStatic;
        Client.AfterStaticChanged += AfterStaticChanged;
        Client.Moved += (x, y) => TilePosition = new Point(x,y);
        #if DEBUG
        Client.LoggedDebug += Console.WriteLine;
        Client.LoggedInfo += Console.WriteLine;
        Client.LoggedWarn += Console.WriteLine;
        Client.LoggedError += Console.WriteLine;
        #endif
        
        if (_sharedTools.Count == 0)
        {
            Tools.Add(new SelectTool()); //Select tool have to be first!
            Tools.Add(new DrawTool());
            Tools.Add(new MoveTool());
            Tools.Add(new ElevateTool());
            Tools.Add(new DeleteTool());
            Tools.Add(new HueTool());
            Tools.Add(new LandBrushTool());
            Tools.Add(new MeshEditTool());
            Tools.Add(new AltitudeGradientTool());
            Tools.Add(new CoastlineTool());

            Tools.ForEach(t => t.PostConstruct(this));
            _activeTool = Tools[0];
        }
        OnWindowsResized(window);
    }
    
    private void OnConnected()
    {
        LandTiles = new LandObject[Client.WidthInTiles, Client.HeightInTiles];
        StaticsManager.Initialize(Client.WidthInTiles, Client.HeightInTiles);
        VirtualLayer.Width = Client.WidthInTiles;
        VirtualLayer.Height = Client.HeightInTiles;
    }

    private void OnDisconnected()
    {
        Reset();
    }

    public void EnableBlockLoading()
    {
        Client.BlockLoaded += OnBlockLoaded;
        Client.BlockUnloaded += OnBlockUnloaded;
    }

    public void DisableBlockLoading()
    {
        Client.BlockLoaded -= OnBlockLoaded;
        Client.BlockUnloaded -= OnBlockUnloaded;
    }

    private void OnBlockLoaded(Block block)
    {
        ClearBlock(block);
        foreach (var landTile in block.LandBlock.Tiles)
        {
            AddTile(landTile);
        }
        foreach (var staticTile in block.StaticBlock.AllTiles())
        {
            StaticsManager.Add(staticTile);
        }
        //Recalculate tiles one and two tiles away from block, to fix corners and normals
        var landBlock = block.LandBlock;
        var minTileX = landBlock.X * 8;
        var maxTileX = minTileX + 7;
        var minTileY = landBlock.Y * 8;
        var maxTileY = minTileY + 7;
        for (var x = minTileX - 2; x <= maxTileX + 2; x++)
        {
            for (var y = minTileY - 2; y <= maxTileY + 2; y++)
            {
                if (!Client.IsValidX(x) || !Client.IsValidY(y))
                    continue;
                var tile = LandTiles?[x, y];
                if (tile != null)
                {
                    _ToRecalculate.Add(tile);
                }
            }
        }

        UpdateLights();
    }

    private void OnBlockUnloaded(Block block)
    {
        var tile = block.LandBlock.Tiles[0];
        if (ViewRange.Contains(tile.X, tile.Y))
        {
            return;
        }
        foreach (var landTile in block.LandBlock.Tiles)
        {
            RemoveTiles(landTile.X, landTile.Y);
        }
        block.Disposed = true;
    }

    private void OnLandTileReplaced(LandTile tile, ushort newId, sbyte newZ)
    {
        var landTile = LandTiles[tile.X, tile.Y];
        if (landTile != null)
        {
            _ToRecalculate.Add(landTile);
        }
    }

    public void OnLandTileElevated(LandTile tile, sbyte newZ)
    {
        for (int x = -2; x < 2; x++)
        {
            for (int y = -2; y < 2; y++)
            {
                var newX = tile.X + x;
                var newY = tile.Y + y;
                if (!Client.IsValidX(newX) || !Client.IsValidY(newY))
                    continue;

                var landObject = LandTiles[newX, newY];
                if (landObject != null)
                {
                    _ToRecalculate.Add(landObject);
                }
            }
        }
    }
    
    private void HueStatic(StaticTile tile, ushort newHue)
    {
        StaticsManager.Get(tile)?.UpdateHue(newHue);
    }
    
    private void AfterStaticChanged(StaticTile tile)
    {
        foreach (var staticObject in StaticsManager.Get(tile.X, tile.Y))
        {
            staticObject.UpdateDepthOffset();
        }
    }

    private void AddTile(LandTile landTile)
    {
        var lo = new LandObject(landTile, this);
        LandTiles[landTile.X, landTile.Y] = lo;
        LandTilesIdDictionary.Add(lo.ObjectId, lo);
        LandTilesCount++;
    }

    public void ReloadShader()
    {
        if(File.Exists("MapEffect.fxc")) 
            MapEffect = new MapEffect(_gfxDevice, File.ReadAllBytes("MapEffect.fxc"));
    }

    public void Load(string clientPath)
    {
        LoadSharedAssets(clientPath);
        Client.InitTileData(_sharedLandTileData, _sharedStaticTileData);
    }

    private void LoadSharedAssets(string clientPath)
    {
        if (_loadedClientPath == clientPath)
            return;
        var tiledataFile = Path.Combine(clientPath, "tiledata.mul");
        var clientVersion = new FileInfo(tiledataFile).Length switch
        {
            >= 3188736 => ClientVersion.CV_7090,
            >= 1644544 => ClientVersion.CV_7000,
            _ => ClientVersion.CV_6000
        };
        _assets = new UOFileManager(clientVersion, clientPath);
        //We don't _assets.Load() as we don't need all the assets
        _assets.Arts.Load();
        _assets.Hues.Load();
        _assets.TileData.Load();
        _assets.Texmaps.Load();
        _assets.AnimData.Load();
        _assets.Lights.Load();
        _assets.Multis.Load();

        _animatedStaticsManager = new AnimatedStaticsManager();
        _animatedStaticsManager.Initialize();
        _sharedArts = new Art(_assets.Arts, _assets.Hues, _gfxDevice);
        _sharedTexmaps = new Texmap(_assets.Texmaps, _gfxDevice);
        HuesManager.Load(_gfxDevice);
        LightsManager.Load(_gfxDevice);
        NonWalkableHue = HuesManager.Instance.GetRGBVector(Color.FromArgb(50, 0, 0));
        WalkableHue = HuesManager.Instance.GetRGBVector(Color.FromArgb(0, 50, 0));

        var tdl = _assets.TileData;
        _sharedValidLandIds.Clear();
        for (var i = 0; i < tdl.LandData.Length; i++)
        {
            var isArtValid = _assets.Arts.File.GetValidRefEntry(i).Length > 0;

            var texId = tdl.LandData[i].TexID;
            var isTexValid = _assets.Texmaps.File.GetValidRefEntry(texId).Length > 0;

            // Only show tiles if art OR texture is valid
            if (isArtValid || isTexValid)
            {
                _sharedValidLandIds.Add((ushort)i);
            }
        }
        _sharedValidStaticIds.Clear();
        for (var i = 0; i < tdl.StaticData.Length; i++)
        {
            if (!_assets.Arts.File.GetValidRefEntry(i + ArtLoader.MAX_LAND_DATA_INDEX_COUNT).Equals
                    (UOFileIndex.Invalid))
            {
                _sharedValidStaticIds.Add((ushort)i);
            }
        }
        _sharedLandTileData = tdl.LandData.Select(ltd => new TileDataLand((ulong)ltd.Flags, ltd.TexID, ltd.Name)).ToArray();
        _sharedStaticTileData = tdl.StaticData.Select(std => new TileDataStatic((ulong)std.Flags, std.Weight, std.Layer, std.Count, std.AnimID, std.Hue, std.LightIndex, std.Height, std.Name)).ToArray();

        _sharedBlueprints = new BlueprintManager(_assets.Multis);
        _sharedBlueprints.Load();
        _loadedClientPath = clientPath;
    }

    public Vector2 Position
    {
        get => new(Camera.Position.X, Camera.Position.Y);
        set
        {
            Camera.Position.X = value.X;
            Camera.Position.Y = value.Y;
            Client.InternalSetPos((ushort)(value.X / TILE_SIZE), (ushort)(value.Y / TILE_SIZE));
        }
    }

    public void Move(float xDelta, float yDelta)
    {
        var oldPos = (Position.X, Position.Y);
        Position = new Vector2(oldPos.X + xDelta, oldPos.Y + yDelta);
    }

    public Point TilePosition
    {
        get => new(Client.X, Client.Y);
        set
        {
            Camera.Position.X = value.X * TILE_SIZE;
            Camera.Position.Y = value.Y * TILE_SIZE;
            Client.InternalSetPos((ushort)value.X, (ushort)value.Y);
        }
    }

    public static Vector2 ScreenToMapCoordinates(float x, float y)
    {
        return new Vector2(x * RSQRT2 - y * -RSQRT2, x * -RSQRT2 + y * RSQRT2);
    }

    private Dictionary<int, TileObject> LandTilesIdDictionary = new();
    
    public LandObject?[,] LandTiles;
    public int LandTilesCount;
    public Dictionary<LandObject, LandObject> GhostLandTiles = new();

    private LandObject? GetLandObject(int x, int y)
    {
        var tiles = LandTiles;
        if ((uint)x >= (uint)tiles.GetLength(0) || (uint)y >= (uint)tiles.GetLength(1))
            return null;
        return tiles[x, y];
    }

    public StaticsManager StaticsManager = new();
    public VirtualLayerObject VirtualLayer = VirtualLayerObject.Instance; //Used for drawing
    public ImageOverlay ImageOverlay = new(); //Used for image overlay feature

    public void UpdateAllTiles()
    {
        foreach (var tile in LandTilesIdDictionary.Values)
        {
            if (tile is LandObject lo)
            {
                lo.Update();
            }
        }
        StaticsManager.UpdateAll();
    }

    public LandTile? GetLandTile(int x, int y)
    {
        var realLandTile = GetLandObject(x, y);
        if (realLandTile == null)
            return null;
        if (GhostLandTiles.TryGetValue(realLandTile, out var ghostLandTile))
        {
            return ghostLandTile.LandTile;
        }
        return realLandTile.LandTile;
    }

    public bool TryGetLandTile(int x, int y, [MaybeNullWhen(false)] out LandTile result)
    {
        result = GetLandTile(x, y);
        return result != null;
    }

    public void ClearBlock(Block block)
    {
        var minX = block.LandBlock.X * 8;
        var maxX = minX + 8;
        var minY = block.LandBlock.Y * 8;
        var maxY = minY + 8;
        for (var x = minX; x < maxX; x++)
        {
            for (var y = minY; y < maxY; y++)
            {
                RemoveTiles((ushort)x, (ushort)y);
            }
        }
    }

    public void RemoveTiles(ushort x, ushort y)
    {
        var lo = LandTiles[x, y];
        if (lo != null)
        {
            LandTiles[x, y] = null;
            LandTilesIdDictionary.Remove(lo.ObjectId);
            LandTilesCount--;
        }
        StaticsManager.Remove(x, y);
    }

    public IEnumerable<TileObject> GetTiles(TileObject? t1, TileObject? t2, bool topTilesOnly)
    {
        if (t1 == null || t2 == null)
            yield break;
        var mx = t1.Tile.X < t2.Tile.X ? (t1.Tile.X, t2.Tile.X) : (t2.Tile.X, t1.Tile.X);
        var my = t1.Tile.Y < t2.Tile.Y ? (t1.Tile.Y, t2.Tile.Y) : (t2.Tile.Y, t1.Tile.Y);
        for (var x = mx.Item1; x <= mx.Item2; x++)
        {
            for (var y = my.Item1; y <= my.Item2; y++)
            {
                if (UseVirtualLayer)
                {
                    yield return new VirtualLayerTile(x, y, (sbyte)VirtualLayerZ);
                }
                else
                {
                    var staticTiles = StaticsManager.Get(x, y).Where(CanDrawStatic);
                    if (topTilesOnly)
                    {
                        var topTile = staticTiles.LastOrDefault();
                        if (topTile != null)
                        {
                            yield return topTile;
                            continue;
                        }
                    }
                    else
                    {
                        foreach (var tile in staticTiles)
                        {
                            yield return tile;
                        }
                    }
                    var landTile = LandTiles[x, y];
                    if (landTile != null && CanDrawLand(landTile))
                    {
                        yield return landTile;
                    }
                }
            }
        }
    }
    
    private MouseState _prevMouseState = Mouse.GetState();
    private bool _IsMouseDragging;
    public RectU16 ViewRange { get; private set; }

    public void Update(GameTime gameTime, bool isActive, bool processMouse, bool processKeyboard)
    {
        if (CEDGame.Closing)
            return;
        if (Client.ServerState != ServerState.Running)
            return;
        
        Metrics.Start("UpdateMap");
        var mouseState = Mouse.GetState();
        var isActiveWorld = ReferenceEquals(this, CEDGame.Worlds.Active?.Map);
        if (isActiveWorld && processMouse)
        {
            if (Client.Running)
            {
                if (PrevSelected != Selected)
                {
                    if (DebugLogging)
                    {
                        Console.WriteLine($"New selected: {Selected?.Tile}");
                    }
                    ActiveTool.OnMouseLeave(PrevSelected);
                    PrevSelected = Selected;
                    ActiveTool.OnMouseEnter(Selected);
                }
            }
            if (isActive)
            {
                if (Selected != null)
                {
                    if (_prevMouseState.LeftButton == ButtonState.Released && mouseState.LeftButton == ButtonState.Pressed)
                    {
                        ActiveTool.OnMousePressed(Selected);
                    }
                }
                if (_prevMouseState.LeftButton == ButtonState.Pressed && mouseState.LeftButton == ButtonState.Released)
                {
                    ActiveTool.OnMouseReleased(Selected);
                    ActiveTool.OnMouseLeave(Selected); //Make sure that we leave tile to clear any ghosts
                    Selected = null; //Very dirty way to retrigger OnMouseEnter() after something presumably changed
                }
                if (mouseState.RightButton == ButtonState.Pressed)
                {
                    var mouseDelta = new Vector2(_prevMouseX - ViewMouseX, _prevMouseY - ViewMouseY);
                    if (mouseDelta != Vector2.Zero)
                    {
                        var moveOffset = ScreenToMapCoordinates(mouseDelta.X, mouseDelta.Y) / Camera.Zoom;
                        Move(moveOffset.X, moveOffset.Y);
                        _IsMouseDragging = true;
                    }
                }
                if (mouseState.RightButton == ButtonState.Released)
                {
                    if (_IsMouseDragging)
                    {
                        _IsMouseDragging = false;
                    }
                    else if (!_IsMouseDragging && _prevMouseState.RightButton == ButtonState.Pressed)
                    {
                        CEDGame.UIManager.OpenContextMenu(RealSelected);
                    }
                }
                if (mouseState.MiddleButton == ButtonState.Pressed)
                {
                    var mouseDelta = new Vector2(_prevMouseX - ViewMouseX, _prevMouseY - ViewMouseY);
                    if (mouseDelta != Vector2.Zero)
                    {
                        var mod = 0.5f;
                        Camera.Pitch -= mouseDelta.Y * mod;
                        Camera.Roll += mouseDelta.X * mod;
                    }
                }
                if (mouseState.ScrollWheelValue != _prevMouseState.ScrollWheelValue)
                {
                    var scrollDelta = (mouseState.ScrollWheelValue - _prevMouseState.ScrollWheelValue) / 1200f;
                    if (Config.Instance.LegacyMouseScroll ^ (_keymap.IsKeyDown(Keys.LeftControl) || _keymap.IsKeyDown
                            (Keys.RightControl)))
                    {
                        if (Selected != null)
                            Selected.Tile.Z += (sbyte)(scrollDelta * 10);
                    }
                    else
                    {
                        Camera.ZoomIn(scrollDelta);
                    }
                }
            }
            else
            {
                ActiveTool.OnMouseReleased(Selected);
            }
        }
        else if (isActiveWorld)
        {
            ActiveTool.OnMouseLeave(PrevSelected);
            ActiveTool.OnMouseReleased(PrevSelected);
            Selected = null;
        }
        _prevMouseState = mouseState;
        _prevMouseX = ViewMouseX;
        _prevMouseY = ViewMouseY;

        if (isActiveWorld && processKeyboard)
        {
            foreach (var key in _keymap.GetKeysReleased())
            {
                ActiveTool.OnKeyReleased(key);
            }
            if (isActive)
            {
                var delta = _keymap.IsKeyDown(Keys.LeftShift) ? 30 : 10;

                foreach (var key in _keymap.GetKeysPressed())
                {
                    ActiveTool.OnKeyPressed(key);
                }

                if (mouseState.LeftButton == ButtonState.Released)
                {
                    foreach (var tool in Tools)
                    {
                        if (_keymap.IsKeyPressed(tool.Shortcut))
                        {
                            if (tool == ActiveTool)
                                tool.OpenPopup();
                            else
                            {
                                tool.ClosePopup();
                                ActiveTool = tool;
                            }
                            break;
                        }
                    }
                }
                if (_keymap.IsActionPressed(Keymap.ToggleAnimatedStatics))
                {
                    AnimatedStatics = !AnimatedStatics;
                }
                if (_keymap.IsActionPressed(Keymap.Minimap))
                {
                    var minimapWindow = CEDGame.UIManager.GetWindow<MinimapWindow>();
                    minimapWindow.Show = !minimapWindow.Show;
                }
                else
                {
                    if (_keymap.IsKeyDown(Keys.LeftControl) || _keymap.IsKeyDown(Keys.RightControl))
                    {
                        if (_keymap.IsKeyDown(Keys.LeftShift) || _keymap.IsKeyDown(Keys.RightShift))
                        {
                            if (_keymap.IsKeyPressed(Keys.Z))
                            {
                                Client.Redo();
                            }
                        }
                        else if (_keymap.IsKeyPressed(Keys.Z))
                        {
                            Client.Undo();
                        }

                        if (_keymap.IsKeyPressed(Keys.R))
                        {
                            Reset();
                        }
                        if (_keymap.IsKeyPressed(Keys.W))
                        {
                            WalkableSurfaces = !WalkableSurfaces;
                        }
                        if (_keymap.IsKeyPressed(Keys.F))
                        {
                            FlatView = !FlatView;
                            UpdateAllTiles();
                        }
                        if (_keymap.IsKeyPressed(Keys.H))
                        {
                            FlatShowHeight = !FlatShowHeight;
                        }
                        if (_keymap.IsKeyPressed(Keys.G))
                        {
                            ShowGrid = !ShowGrid;
                        }
                    }
                    else
                    {
                        if (_keymap.IsKeyPressed(Keys.Escape))
                        {
                            Camera.ResetCamera();
                        }
                        if (_keymap.IsActionDown(Keymap.MoveLeft))
                        {
                            Move(-delta, delta);
                        }
                        if (_keymap.IsActionDown(Keymap.MoveRight))
                        {
                            Move(delta, -delta);
                        }
                        if (_keymap.IsActionDown(Keymap.MoveUp))
                        {
                            Move(-delta, -delta);
                        }
                        if (_keymap.IsActionDown(Keymap.MoveDown))
                        {
                            Move(delta, delta);
                        }
                    }
                }
            }
        }

        Camera.Update();
        if (Client.Running)
        {
            var newViewRange = CalculateViewRange(Camera);
            if (ViewRange != newViewRange)
            {
                ViewRange = newViewRange;
                Client.RequestBlocks(ViewRange);
            }
        }
        else
        {
            ViewRange = default;
        }
        if (Client.Running && AnimatedStatics)
        {
            if (this == CEDGame.Worlds.Active?.Map)
                _animatedStaticsManager.Process(gameTime);
            foreach (var animatedStaticTile in StaticsManager.AnimatedTiles)
            {
                animatedStaticTile.UpdateId();
                animatedStaticTile.Update();
            }
        }
        foreach (var landObject in _ToRecalculate)
        {
            if (GhostLandTiles.TryGetValue(landObject, out var ghostLandObject))
            {
                ghostLandObject.Update();
            }
            landObject.Update();
        }
        _ToRecalculate.Clear();
        Metrics.Stop("UpdateMap");
    }

    public void Reset()
    {
        LandTilesCount = 0;
        
        LandTiles = new LandObject[Client.Width * 8, Client.Height * 8];
        LandTilesIdDictionary.Clear();
        PrevSelected = null;
        Selected = null;
        RealSelected = null;
        GhostLandTiles.Clear();
        StaticsManager.Clear();
        ViewRange = default;
        Client.ResetCache();
    }

    public void UpdateLights()
    {
        foreach (var light in StaticsManager.LightTiles.Values)
        {
            light.Update();   
        }
    }

    private TileObject? PrevSelected;
    public TileObject? Selected { get; private set; }
    public TileObject? RealSelected { get; private set; }

    private void UpdateMouseSelection(int x, int y)
    {
        if (!ViewHovered || !_selectionBuffer.Bounds.Contains(x, y))
        {
            RealSelected = null;
        }
        else
        {
            var pixels = new FNAColor[1];
            _selectionBuffer.GetData(0, new Microsoft.Xna.Framework.Rectangle(x, y, 1, 1), pixels, 0, 1);
            var pixel = pixels[0];
            var selectedIndex = pixel.R | (pixel.G << 8) | (pixel.B << 16);
            if (selectedIndex < 1)
                RealSelected = null;
            else
                RealSelected = LandTilesIdDictionary.TryGetValue(selectedIndex, out var lo) ? lo : StaticsManager.Get(selectedIndex);
        }
        
        Selected = RealSelected;
        
        if (UseVirtualLayer)
        {
            var z = FlatView ? 0 : VirtualLayerZ;
            var virtualLayerPos = Unproject(x, y, z);
            var newX = (ushort)Math.Clamp(virtualLayerPos.X + 1, 0, Client.Width * 8 - 1);
            var newY = (ushort)Math.Clamp(virtualLayerPos.Y + 1, 0, Client.Height * 8 - 1);
            if (newX != PrevSelected?.Tile.X || newY != PrevSelected.Tile.Y)
            {
                Selected = new VirtualLayerTile(newX, newY, (sbyte)z);
            }
            else
                Selected = PrevSelected;
        }
    }

    private RectU16 CalculateViewRange(Camera camera)
    {
        float zoom = camera.Zoom;
        int screenWidth = camera.ScreenSize.Width;
        int screenHeight = camera.ScreenSize.Height;
        
        // Calculate the size of the drawing diamond in pixels
        // Default is 2.0f, but 2.6f gives smaller viewrange without any visible problems 
        float screenDiamondDiagonal = (screenWidth + screenHeight) / zoom / 2.6f;
        
        Vector3 center = camera.Position;
        
        // Render a few extra rows at the top to deal with things at lower z
        var minTileX = Client.ClampX((int)((center.X - screenDiamondDiagonal) / TILE_SIZE - 8));
        var minTileY = Client.ClampY((int)((center.Y - screenDiamondDiagonal) / TILE_SIZE - 8));
        
        // Render a few extra rows at the bottom to deal with things at higher z
        var maxTileX = Client.ClampX((int)((center.X + screenDiamondDiagonal) / TILE_SIZE + 8));
        var maxTileY = Client.ClampY((int)((center.Y + screenDiamondDiagonal) / TILE_SIZE + 8));
        
        return new RectU16(minTileX, minTileY, maxTileX, maxTileY);
    }

    public Vector3 Unproject(int x, int y, int z)
    {
        var worldPoint = ViewportFromCamera().Unproject
        (
            new FNAVector3(x, y, -(z / 384f) + 0.5f),
            Camera.FnaWorldViewProj,
            Matrix.Identity,
            Matrix.Identity
        );
        return new Vector3
        (
            worldPoint.X / TILE_SIZE,
            worldPoint.Y / TILE_SIZE,
            worldPoint.Z / TILE_Z_SCALE
        );
    }

    private bool CanDrawLand(LandObject lo)
    {
        if(!ShowLand || (lo.Tile.Id <= 2 && !ShowNoDraw)) 
            return false;
        return WithinZRange(lo.Tile.Z);
    }

    public bool CanDrawStatic(StaticObject so)
    {
        var tile = so.StaticTile;
        var id = tile.Id;
        if (id >= UoFileManager.TileData.StaticData.Length)
            return false;

        ref StaticTiles data = ref UoFileManager.TileData.StaticData[id];

        // Outlands specific
        // if ((data.Flags & TileFlag.NoDraw) != 0)
        //     return false;

        if (!ShowNoDraw)
        {
            switch (id)
            {
                case 0x0001:
                case 0x21BC:
                case 0x63D3: return false;

                case 0x9E4C:
                case 0x9E64:
                case 0x9E65:
                case 0x9E7D: return (data.Flags & TileFlag.Background) == 0 
                            // && (data.Flags & TileFlag.NoDraw) == 0 // Outlands specific
                            && (data.Flags & TileFlag.Surface) == 0;

                case 0x2198:
                case 0x2199:
                case 0x21A0:
                case 0x21A1:
                case 0x21A2:
                case 0x21A3:
                case 0x21A4: return false;
            }
        }
        if (!Client.IsValidX(tile.X) || !Client.IsValidY(tile.Y))
        {
            return false;
        }
        if (!ShowStatics)
            return false;
        
        var landTile = LandTiles[tile.X, tile.Y];
        if (!WithinZRange(tile.Z) || !FlatView && landTile != null && CanDrawLand(landTile) && 
            WithinZRange(landTile.Tile.Z) && landTile.AverageZ() >= tile.PriorityZ + 5)
            return false;

        var show = so.Visible;
        
        if(show && ObjectIdFilterEnabled)
        {
            show &= !(ObjectIdFilterInclusive ^ ObjectIdFilter.Contains(id));
        }
        if(show && ObjectHueFilterEnabled)
        {
            show &= !(ObjectHueFilterInclusive ^ ObjectHueFilter.Contains(tile.Hue));
        }
        return show;
    }

    private static Vector4 NonWalkableHue;
    private static Vector4 WalkableHue;
    public Vector4 GhostLandTilesHue = Vector4.Zero;
    
    public bool IsWalkable(LandObject lo)
    {
        //TODO: Save value for later to avoid calculation, recalculate whole cell on operations
        if (lo.Walkable.HasValue)
            return lo.Walkable.Value;
        
        var landTile = lo.LandTile;
        bool walkable = !UoFileManager.TileData.LandData[landTile.Id].IsImpassable;
        if (!walkable)
        {
            return false;
        }
        var staticObjects = StaticsManager.Get(landTile.X, landTile.Y);
        if (staticObjects != null)
        {
            foreach (var so in staticObjects)
            {
                var staticTile = so.StaticTile;
                var staticTileData = UoFileManager.TileData.StaticData[staticTile.Id];
                var ok = staticTile.Z + staticTileData.Height <= landTile.Z || landTile.Z + 16 <= staticTile.Z;
                if (!ok && !staticTileData.IsSurface && staticTileData.IsImpassable)
                {
                    return false;
                }
            }
        }
        return true;
    }
    
    public bool IsWalkable(StaticObject so)
    {
        if (so.Walkable.HasValue)
            return so.Walkable.Value;
        
        var tile = so.StaticTile;
        var thisTileData = UoFileManager.TileData.StaticData[tile.Id];
        var thisCalculatedHeight = thisTileData.IsBridge ? thisTileData.Height / 2 : thisTileData.Height;
        bool walkable = !thisTileData.IsImpassable;
        if (walkable)
        {
            var staticObjects = StaticsManager.Get(tile.X, tile.Y);
            if (staticObjects != null)
            {
                foreach (var so2 in staticObjects)
                {
                    var staticTile = so2.StaticTile;
                    var staticTileData = UoFileManager.TileData.StaticData[staticTile.Id];
                    var ok = staticTile.Z + staticTileData.Height <= tile.Z + thisCalculatedHeight || tile.Z + 16 <= staticTile.Z;
                    if (!ok && !staticTileData.IsSurface && staticTileData.IsImpassable)
                    {
                        return false;
                    }
                }
            }
        }
        return true;
    }

    private bool WithinZRange(short z)
    {
        return z >= MinZ && z <= MaxZ;
    }

    private bool DrawStatic(StaticObject so, Vector4 hueOverride = default)
    {
        if (!CanDrawStatic(so))
            return false;
        
        _mapRenderer.DrawMapObject(so, hueOverride);
        return true;
    }
    
    private void DrawLand(LandObject lo, Vector4 hueOverride = default)
    {
        var landTile = lo.LandTile;
        if (landTile.Id > UoFileManager.TileData.LandData.Length)
            return;
        if (!CanDrawLand(lo))
            return;

        _mapRenderer.DrawMapObject(lo, hueOverride);
    }

    public void Draw()
    {
        Metrics.Start("DrawMap");
        if (!Client.Running || CEDGame.Closing)
        {
            DrawBackground();
            return;
        }
        Metrics.Measure("DrawSelection", DrawSelectionBuffer);
        Metrics.Start("GetMouseSelection");
        UpdateMouseSelection(ViewMouseX, ViewMouseY);
        Metrics.Stop("GetMouseSelection");
        if (DebugDrawSelectionBuffer)
            return;

        Metrics.Measure("DrawLights", () => DrawLights(Camera));
        if (DebugDrawLightMap)
            return;

        _mapRenderer.SetRenderTarget(_worldRenderTarget, new FNARectangle(0, 0, _worldRenderTarget.Width, _worldRenderTarget.Height));
        Metrics.Measure("DrawImageOverlayBelow", () => DrawImageOverlay(false));
        Metrics.Measure("DrawLand", () => DrawLand(Camera, ViewRange));
        Metrics.Start("DrawLandGrid");
        if (ShowGrid)
        {
            DrawLand(Camera, ViewRange, "TerrainGrid");
        }
        Metrics.Stop("DrawLandGrid");
        Metrics.Measure("DrawLandHeight", DrawLandHeight);
        Metrics.Measure("DrawStatics", () => DrawStatics(Camera, ViewRange));
        Metrics.Measure("DrawImageOverlayAbove", () => DrawImageOverlay(true));
        Metrics.Measure("ApplyLights", ApplyLights);
        Metrics.Measure("DrawVirtualLayer", DrawVirtualLayer);
        Metrics.Stop("DrawMap");    
    }

    public void AfterDraw()
    {
        if (Export)
        {
            ExportImage();
            Export = false;
        }
    }

    private void DrawBackground()
    {
        _mapRenderer.SetRenderTarget(_worldRenderTarget, new FNARectangle(0, 0, _worldRenderTarget.Width, _worldRenderTarget.Height));
        _gfxDevice.BlendState = BlendState.AlphaBlend;
        _spriteBatch.Begin();
        var backgroundRect = new FNARectangle
        (
            _worldRenderTarget.Width / 2 - _background.Width / 2,
            _worldRenderTarget.Height / 2 - _background.Height / 2,
            _background.Width,
            _background.Height
        );
        _spriteBatch.Draw(_background, backgroundRect, FNAColor.White);
        _spriteBatch.End();
    }

    private void DrawSelectionBuffer()
    {
        MapEffect.WorldViewProj = Camera.FnaWorldViewProj;
        MapEffect.CurrentTechnique = MapEffect.Techniques["Selection"];
        _mapRenderer.SetRenderTarget(DebugDrawSelectionBuffer ? null : _selectionBuffer,
            new FNARectangle(0, 0, _selectionBuffer.Width, _selectionBuffer.Height));
        _mapRenderer.Begin
        (
            MapEffect,
            RasterizerState.CullNone,
            SamplerState.PointClamp,
            _DepthStencilState,
            BlendState.AlphaBlend
        );
        foreach (var (x,y) in ViewRange.Iterate())
        {
            var landTile = GetLandObject(x, y);
            if (landTile != null)
            {
                DrawLand(landTile, landTile.ObjectIdColor);
            }

            var tiles = StaticsManager.Get(x, y);
            if(tiles == null) continue;
            foreach (var tile in tiles)
            {
                if (tile.CanDraw)
                {
                    DrawStatic(tile, tile.ObjectIdColor);
                }
            }
        }
        _mapRenderer.End();
    }
    
    private void DrawLights(Camera camera)
    {
        if (LightsManager.Instance.MaxGlobalLight && !LightsManager.Instance.AltLights && !DebugDrawLightMap) 
        {
            return; //Little performance boost
        }
        MapEffect.WorldViewProj = camera.FnaWorldViewProj;
        MapEffect.CurrentTechnique = MapEffect.Techniques["Statics"];
        _mapRenderer.SetRenderTarget(DebugDrawLightMap ? null : _lightMap,
            new FNARectangle(0, 0, _lightMap.Width, _lightMap.Height));
        _gfxDevice.Clear(ClearOptions.Target, LightsManager.Instance.GlobalLightLevelColor, 0f, 0);
        _mapRenderer.Begin
        (
            MapEffect,
            RasterizerState.CullNone, 
            SamplerState.PointClamp,
            DepthStencilState.None, 
            BlendState.Additive
        );
        foreach (var kvp in StaticsManager.LightTiles)
        {
            var staticTile = kvp.Key;
            var light = kvp.Value;
            if (light.CanDraw)
            {
                if (CanDrawStatic(staticTile))
                {
                    _mapRenderer.DrawMapObject(light, default);
                }
            }
        }
        _mapRenderer.End();
    }

    private void DrawLand(Camera camera, RectU16 viewRange, string technique = "Terrain")
    {
        if (!ShowLand)
        {
            return;
        }
        MapEffect.WorldViewProj = camera.FnaWorldViewProj;
        MapEffect.CurrentTechnique = MapEffect.Techniques[technique];
        _mapRenderer.Begin
        (
            MapEffect,
            RasterizerState.CullNone,
            SamplerState.PointClamp,
            _DepthStencilState,
            BlendState.AlphaBlend
        );
           
        foreach (var (x,y) in viewRange.Iterate())
        {
            var tile = GetLandObject(x, y);
            if (tile != null && tile.CanDraw)
            {
                var hueOverride = Vector4.Zero;
                if (WalkableSurfaces && !UoFileManager.TileData.LandData[tile.LandTile.Id].IsWet)
                {
                    hueOverride = IsWalkable(tile) ? WalkableHue : NonWalkableHue;

                }
                DrawLand(tile, hueOverride);
            }
        }
        
        foreach (var tile in GhostLandTiles.Values)
        {
            DrawLand(tile, GhostLandTilesHue);
        }
        _mapRenderer.End();
    }

    private void DrawLandHeight()
    {
        if (!FlatView || !FlatShowHeight)
        {
            return;
        }
        var font = _fontSystem.GetFont(18 * Camera.Zoom);
        var halfTile = TILE_SIZE * 0.5f * Camera.Zoom;
        _spriteBatch.Begin();
        foreach (var (x, y) in ViewRange.Iterate())
        {
            var tile = GetLandObject(x, y);
            if (tile != null && tile.CanDraw)
            {
                DrawTileHeight(tile, font, halfTile);
            }
        }
        foreach (var tile in GhostLandTiles.Values)
        {
            DrawTileHeight(tile, font, halfTile);
        }
        _spriteBatch.End();
    }

    private void DrawTileHeight(LandObject tile, DynamicSpriteFont font, float yOffset)
    {
        var text = tile.LandTile.Z.ToString();
        var halfTextSize = font.MeasureString(text) / 2;
        var tilePos = tile.Vertices[0].Position;
        var projected = ViewportFromCamera().Project
            (new FNAVector3(tilePos.X, tilePos.Y, tilePos.Z), Camera.FnaWorldViewProj, Matrix.Identity, Matrix.Identity);
        var pos = new Vector2
            (projected.X - halfTextSize.X, projected.Y + yOffset);
        if (pos.X > 0 && pos.X < Camera.ScreenSize.Width && pos.Y > 0 &&
            pos.Y < Camera.ScreenSize.Height)
        {
            _spriteBatch.DrawString(font, text, new FNAVector2(pos.X, pos.Y), FNAColor.White);
        }
    }

    private void DrawStatics(Camera camera, RectU16 viewRange)
    {
        if (!ShowStatics)
        {
            return;
        }
        MapEffect.WorldViewProj = camera.FnaWorldViewProj;
        MapEffect.CurrentTechnique = MapEffect.Techniques["Statics"];
        _mapRenderer.Begin
        (
            MapEffect,
            RasterizerState.CullNone,
            SamplerState.PointClamp,
            _DepthStencilState,
            BlendState.AlphaBlend
        );
        foreach (var (x,y) in viewRange.Iterate())
        {
            var tiles = StaticsManager.Get(x, y);
            if(tiles == null) continue;
            foreach (var tile in tiles)
            {
                if (tile.CanDraw)
                {
                    var hueOverride = Vector4.Zero;
                    if (WalkableSurfaces && UoFileManager.TileData.StaticData[tile.Tile.Id].IsSurface)
                    {
                        hueOverride = IsWalkable(tile) ? WalkableHue : NonWalkableHue;
                    }
                    DrawStatic(tile, hueOverride);
                }
            }
        }
        foreach (var tile in StaticsManager.GhostTiles)
        {
            DrawStatic(tile);
        }
        _mapRenderer.End();
    }

    public void ApplyLights()
    {
        if (LightsManager.Instance.MaxGlobalLight && !LightsManager.Instance.AltLights) 
        {
            return; //Skip lighting
        }
        _spriteBatch.Begin(SpriteSortMode.Deferred, LightsManager.Instance.ApplyBlendState);
        _spriteBatch.Draw(_lightMap, FNAVector2.Zero, LightsManager.Instance.ApplyBlendColor);
        _spriteBatch.End();
    }

    public void DrawVirtualLayer()
    {
        if (!ShowVirtualLayer)
        {
            return;
        }
        MapEffect.CurrentTechnique = MapEffect.Techniques["VirtualLayer"];
        _mapRenderer.Begin
        (
            MapEffect,
            RasterizerState.CullNone,
            SamplerState.PointClamp,
            _DepthStencilState,
            BlendState.AlphaBlend
        );
        VirtualLayer.Z = (sbyte)VirtualLayerZ;
        _mapRenderer.DrawMapObject(VirtualLayer, Vector4.Zero);
        _mapRenderer.End();
    }

    public void DrawImageOverlay(bool aboveTerrain)
    {
        if (!ImageOverlay.Enabled || ImageOverlay.Texture == null)
        {
            return;
        }
        if (ImageOverlay.DrawAboveTerrain != aboveTerrain)
        {
            return;
        }
        MapEffect.WorldViewProj = Camera.FnaWorldViewProj;
        MapEffect.CurrentTechnique = MapEffect.Techniques["ImageOverlay"];
        _mapRenderer.Begin
        (
            MapEffect,
            RasterizerState.CullNone,
            SamplerState.LinearClamp,
            DepthStencilState.None,
            BlendState.AlphaBlend
        );
        _mapRenderer.DrawMapObject(ImageOverlay, Vector4.Zero);
        _mapRenderer.End();
    }

    public bool Export = false;
    public string ExportPath = "render.png";
    public int ExportWidth = 1920;
    public int ExportHeight = 1080;
    public float ExportZoom = 1.0f;
    
    public void ExportImage()
    {
        var pp = _gfxDevice.PresentationParameters;
        if (ExportWidth != pp.BackBufferWidth || ExportHeight != pp.BackBufferHeight)
        {
            pp.BackBufferWidth = ExportWidth;
            pp.BackBufferHeight = ExportHeight;
            pp.DeviceWindowHandle = CEDGame.Window.Handle;
            _gfxDevice.Reset(pp);
        }
        var myRenderTarget = new RenderTarget2D(_gfxDevice, ExportWidth, ExportHeight, false, SurfaceFormat.Color, DepthFormat.Depth24);
        var newLightMap = new RenderTarget2D
        (
            _gfxDevice,
            ExportWidth,
            ExportHeight,
            _lightMap.LevelCount >  1,
            _lightMap.Format,
            _lightMap.DepthStencilFormat
        );
        _lightMap.Dispose();
        _lightMap = newLightMap;
        
        var myCamera = new Camera();
        myCamera.Position = Camera.Position;
        myCamera.Zoom = ExportZoom;
        var rbounds = myRenderTarget.Bounds;
        myCamera.ScreenSize = new Rectangle(rbounds.X, rbounds.Y, rbounds.Width, rbounds.Height);
        myCamera.Update();
        
        var cameraBounds = CalculateViewRange(myCamera);
        Client.RequestBlocks(cameraBounds);
        while(Client.WaitingForBlocks) 
            Client.Update();
        
        foreach (var landObject in _ToRecalculate)
        {
            landObject.Update();
        }
        _ToRecalculate.Clear();
        
        MapEffect.WorldViewProj = myCamera.FnaWorldViewProj;
        DrawLights(myCamera);
        _mapRenderer.SetRenderTarget(myRenderTarget, new FNARectangle(0,0, ExportWidth, ExportHeight));
        DrawLand(myCamera, cameraBounds);
        DrawStatics(myCamera, cameraBounds);
        ApplyLights();
        using var fs = new FileStream(ExportPath, FileMode.OpenOrCreate);
        if(ExportPath.EndsWith(".png"))
            myRenderTarget.SaveAsPng(fs, myRenderTarget.Width, myRenderTarget.Height);
        else
        {
            if (!ExportPath.EndsWith(".jpg"))
            {
                Console.WriteLine("[EXPORT], invalid file format, exporting as JPEG");
            }
            myRenderTarget.SaveAsJpeg(fs, myRenderTarget.Width, myRenderTarget.Height);
        }
        _mapRenderer.SetRenderTarget(null);
        myRenderTarget.Dispose();
        OnWindowsResized(_gameWindow);
    }

    public void OnWindowsResized(GameWindow window)
    {
        var windowSize = window.ClientBounds;
        Resize(windowSize.Width, windowSize.Height);
    }

    private int _pendingWidth, _pendingHeight;

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (_worldRenderTarget != null && Camera.ScreenSize.Width == width && Camera.ScreenSize.Height == height)
            return;
        if (_worldRenderTarget != null && (width != _pendingWidth || height != _pendingHeight))
        {
            _pendingWidth = width;
            _pendingHeight = height;
            return;
        }

        Camera.ScreenSize = new Rectangle(0, 0, width, height);
        Camera.Update();

        _selectionBuffer?.Dispose();
        _selectionBuffer = new RenderTarget2D(_gfxDevice, width, height, false, SurfaceFormat.Color, DepthFormat.Depth24);
        _lightMap?.Dispose();
        _lightMap = new RenderTarget2D(_gfxDevice, width, height, false, SurfaceFormat.Color, DepthFormat.None);
        _worldRenderTarget?.Dispose();
        _worldRenderTarget = new RenderTarget2D(_gfxDevice, width, height, false, SurfaceFormat.Color, DepthFormat.Depth24);
    }

    private Viewport ViewportFromCamera() => new(0, 0, Camera.ScreenSize.Width, Camera.ScreenSize.Height);

    public void Dispose()
    {
        DisableBlockLoading();
        _selectionBuffer?.Dispose();
        _lightMap?.Dispose();
        _worldRenderTarget?.Dispose();
    }
}