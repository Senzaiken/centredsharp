using System.Reflection;
using CentrED.Map;
using CentrED.UI;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using static CentrED.Application;
using static SDL3.SDL;

namespace CentrED;

public class CentrEDGame : Game
{
    public readonly GraphicsDeviceManager _gdm;

    private Keymap _keymap;
    public WorldManager Worlds = new();
    public MapManager MapManager => Worlds.Active!.Map;
    public UIManager UIManager;
    public FacetController FacetController;
    public bool Closing { get; set; }
    
    public CentrEDGame()
    {
        _gdm = new GraphicsDeviceManager(this)
        {
            IsFullScreen = false,
            PreferredDepthStencilFormat = DepthFormat.Depth24
        };

        _gdm.PreparingDeviceSettings += (sender, e) =>
        {
            e.GraphicsDeviceInformation.PresentationParameters.RenderTargetUsage =
                RenderTargetUsage.DiscardContents;
        };
        var appName = Assembly.GetExecutingAssembly().GetName();
        SDL_SetWindowTitle(Window.Handle, $"{appName.Name} {appName.Version}");

        SDL_ShowCursor();
        SDL_SetWindowResizable(Window.Handle, true);
        Window.ClientSizeChanged += OnWindowResized;
    }
    
    protected override void Initialize()
    {
        if (_gdm.GraphicsDevice.Adapter.IsProfileSupported(GraphicsProfile.HiDef))
        {
            _gdm.GraphicsProfile = GraphicsProfile.HiDef;
        }

        _gdm.ApplyChanges();

        Log.Start(LogTypes.All);
        LangManager.Load();
        //UIManager have to exist before MapManager, since Tools can be dependent on Windows
        _keymap =  new Keymap();
        UIManager = new UIManager(_gdm.GraphicsDevice, Window, _keymap);
        Worlds.Initialize(_gdm.GraphicsDevice, Window, _keymap);
        Worlds.CreateWorld(Application.BootstrapClient);
        FacetController = new FacetController();
        FacetController.Refresh();
        FacetController.SyncWorlds();

        base.Initialize();
    }

    protected override void BeginRun()
    {
        base.BeginRun();
        SDL_MaximizeWindow(Window.Handle);
    }

    protected override void UnloadContent()
    {
        Metrics.StopProfiling();
        CEDClient.Disconnect();
        FacetController?.Shutdown();
    }

    protected override void Update(GameTime gameTime)
    {
        try
        {
            _keymap.Update(Keyboard.GetState());
            FacetController.ProcessSync();
            foreach (var world in Worlds.Worlds)
            {
                if (world.Map.WindowVisible)
                    FacetController.EnsureLoaded(world);
            }
            Metrics.Start("UpdateClient");
            foreach (var world in Worlds.Worlds)
            {
                if (world.Client.Running && !FacetController.IsBusy(world))
                    world.Client.Update();
            }
            Metrics.Stop("UpdateClient");
            foreach (var world in Worlds.Worlds)
            {
                if (FacetController.IsBusy(world) || !world.Map.WindowVisible)
                    continue;
                world.Map.Update(gameTime, IsActive, world.Map.ViewHovered, world.Map.ViewFocused);
            }
            Config.AutoSave();
        }
        catch(Exception e)
        {
            UIManager.ReportCrash(e);
        }
        base.Update(gameTime);
    }

    protected override bool BeginDraw()
    {
        Metrics.Start("BeginDraw");
        //We can rely on UIManager, since it draws UI over the main window as well as handles to all the extra windows
        var maxWindowSize = UIManager.MaxWindowSize();
        var width = (int)maxWindowSize.X;
        var height = (int)maxWindowSize.Y;
        if (width > 0 && height > 0)
        {
            var pp = GraphicsDevice.PresentationParameters;
            if (width != pp.BackBufferWidth || height != pp.BackBufferHeight)
            {
                pp.BackBufferWidth = width;
                pp.BackBufferHeight = height;
                pp.DeviceWindowHandle = Window.Handle;
                GraphicsDevice.Reset(pp);
            }
        }
        Metrics.Stop("BeginDraw");
        return base.BeginDraw();
    }

    protected override void Draw(GameTime gameTime)
    {
        if (gameTime.ElapsedGameTime.Ticks > 0)
        {
            try
            {
                Metrics.Start("Draw");
                foreach (var world in Worlds.Worlds)
                {
                    if (FacetController.IsBusy(world) || !world.Map.WindowVisible)
                        continue;
                    world.Map.Draw();
                }
                GraphicsDevice.SetRenderTarget(null);
                GraphicsDevice.Clear(Color.Black);
                UIManager.Draw();
                Present();
                UIManager.DrawExtraWindows();
                foreach (var world in Worlds.Worlds)
                    world.Map.AfterDraw();
                Metrics.Stop("Draw");
                Metrics.CaptureFrame();
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                UIManager.ReportCrash(e);
            }
        }
        base.Draw(gameTime);
    }

    private void Present()
    {
        Rectangle bounds = Window.ClientBounds;
        GraphicsDevice.Present(
            new Rectangle(0, 0, bounds.Width, bounds.Height),
            null,
            Window.Handle
        );
    }

    protected override void EndDraw()
    {
        //Restore main window viewport and scissor rectangle for next tick Update()
        var gameWindowRect = Window.ClientBounds;
        GraphicsDevice.Viewport = new Viewport(0, 0, gameWindowRect.Width, gameWindowRect.Height);
        GraphicsDevice.ScissorRectangle = new Rectangle(0, 0, gameWindowRect.Width, gameWindowRect.Height);
    }

    private void OnWindowResized(object? sender, EventArgs e)
    {
        GameWindow window = sender as GameWindow;
        if (window != null)
            MapManager.OnWindowsResized(window);
    }
}