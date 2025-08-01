#region License
/* FNA - XNA4 Reimplementation for Desktop Platforms
 * Copyright 2009-2024 Ethan Lee and the MonoGame Team
 *
 * Released under the Microsoft Public License.
 * See LICENSE for details.
 */
#endregion

#region Using Statements
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using SDL3;

using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
#endregion

namespace Microsoft.Xna.Framework
{
	internal static unsafe class SDL3_FNAPlatform
	{
		#region Static Constants

		private static string OSVersion;

		private static readonly bool UseScancodes = Environment.GetEnvironmentVariable(
			"FNA_KEYBOARD_USE_SCANCODES"
		) == "1";

		private static bool SupportsGlobalMouse;

		private static bool SupportsOrientations;

		#endregion

		#region Game Objects

		/* This is needed for asynchronous window events */
		private static List<Game> activeGames = new List<Game>();

		#endregion

		#region Init/Exit Methods

		public static string ProgramInit(LaunchParameters args)
		{
			// This is how we can weed out cases where fnalibs is missing
			try
			{
				OSVersion = SDL.SDL_GetPlatform();
			}
			catch(DllNotFoundException)
			{
				FNALoggerEXT.LogError(
					"SDL3 was not found! Do you have fnalibs?"
				);
				throw;
			}
			catch(BadImageFormatException e)
			{
				string error = string.Format(
					"This process is {0}-bit, the DLL is {1}-bit!",
					(IntPtr.Size == 4) ? "32" : "64",
					(IntPtr.Size == 4) ? "64" : "32"
				);
				FNALoggerEXT.LogError(error);
				throw new BadImageFormatException(error, e);
			}

			/* SDL3 might complain if an OS that uses SDL_main has not actually
			 * used SDL_main by the time you initialize SDL3.
			 * The only platform that is affected is Windows, but we can skip
			 * their WinMain. This was only added to prevent iOS from exploding.
			 * -flibit
			 */
			SDL.SDL_SetMainReady();

			/* Mount TitleLocation.Path */
			string titleLocation = GetBaseDirectory();

			// If available, load the SDL_GameControllerDB
			string mappingsDB = Path.Combine(
				titleLocation,
				"gamecontrollerdb.txt"
			);
			if (File.Exists(mappingsDB))
			{
				SDL.SDL_SetHint(
					SDL.SDL_HINT_GAMECONTROLLERCONFIG_FILE,
					mappingsDB
				);
			}

			// Are you even surprised this is necessary?
			if (Environment.GetEnvironmentVariable("FNA_NUKE_STEAM_INPUT") == "1")
			{
				SDL.SDL_SetHintWithPriority(
					"SDL_GAMECONTROLLER_IGNORE_DEVICES",
					"0x28DE/0x11FF",
					SDL.SDL_HintPriority.SDL_HINT_OVERRIDE
				);
				SDL.SDL_SetHintWithPriority(
					"SDL_GAMECONTROLLER_IGNORE_DEVICES_EXCEPT",
					"",
					SDL.SDL_HintPriority.SDL_HINT_OVERRIDE
				);

				// This should be redundant, but who knows...
				SDL.SDL_SetHintWithPriority(
					"SDL_GAMECONTROLLER_ALLOW_STEAM_VIRTUAL_GAMEPAD",
					"0",
					SDL.SDL_HintPriority.SDL_HINT_OVERRIDE
				);
			}

			// Built-in SDL3 command line arguments
			string arg;
			if (args.TryGetValue("glprofile", out arg))
			{
				if (arg == "es3")
				{
					SDL.SDL_SetHintWithPriority(
						"FNA3D_OPENGL_FORCE_ES3",
						"1",
						SDL.SDL_HintPriority.SDL_HINT_OVERRIDE
					);
				}
				else if (arg == "core")
				{
					SDL.SDL_SetHintWithPriority(
						"FNA3D_OPENGL_FORCE_CORE_PROFILE",
						"1",
						SDL.SDL_HintPriority.SDL_HINT_OVERRIDE
					);
				}
				else if (arg == "compatibility")
				{
					SDL.SDL_SetHintWithPriority(
						"FNA3D_OPENGL_FORCE_COMPATIBILITY_PROFILE",
						"1",
						SDL.SDL_HintPriority.SDL_HINT_OVERRIDE
					);
				}
			}
			if (args.TryGetValue("angle", out arg) && arg == "1")
			{
				SDL.SDL_SetHintWithPriority(
					"FNA3D_OPENGL_FORCE_ES3",
					"1",
					SDL.SDL_HintPriority.SDL_HINT_OVERRIDE
				);
				SDL.SDL_SetHintWithPriority(
					"SDL_OPENGL_ES_DRIVER",
					"1",
					SDL.SDL_HintPriority.SDL_HINT_OVERRIDE
				);
			}
			if (args.TryGetValue("forcemailboxvsync", out arg) && arg == "1")
			{
				SDL.SDL_SetHintWithPriority(
					"FNA3D_VULKAN_FORCE_MAILBOX_VSYNC",
					"1",
					SDL.SDL_HintPriority.SDL_HINT_OVERRIDE
				);
			}

			/* FIXME: SDL bug!
			 * Well, really it's a Windows bug - for some reason the
			 * Windows audio team has lost it and now you can't just
			 * pick between directsound/wasapi, we have to go back
			 * and forth constantly, so for convenience we're adding
			 * this check. This shouldn't be necessary anywhere else
			 * as far as I know, treat it like an OS bug otherwise!
			 * -flibit
			 */
			if (args.TryGetValue("audiodriver", out arg))
			{
				SDL.SDL_SetHintWithPriority(
					"SDL_AUDIO_DRIVER",
					arg,
					SDL.SDL_HintPriority.SDL_HINT_OVERRIDE
				);
			}

			// This _should_ be the first real SDL call we make...
			if (!SDL.SDL_Init(
				SDL.SDL_InitFlags.SDL_INIT_VIDEO |
				SDL.SDL_InitFlags.SDL_INIT_GAMEPAD
			))
			{
				throw new Exception("SDL_Init failed: " + SDL.SDL_GetError());
			}

			string videoDriver = SDL.SDL_GetCurrentVideoDriver();

			/* A number of platforms don't support global mouse, but
			 * this really only matters on desktop where the game
			 * screen may not be covering the whole display.
			 */
			SupportsGlobalMouse = (	OSVersion.Equals("Windows") ||
						OSVersion.Equals("macOS") ||
						videoDriver.Equals("x11")	);

			// Only iOS and Android care about device orientation.
			SupportsOrientations = ( OSVersion.Equals("iOS") ||
						 OSVersion.Equals("Android")	);

			/* We need to change the Windows default here, as the
			 * display server does not seem to handle focus changes
			 * gracefully like Wayland (and even X11) do. If for
			 * _any_ reason focus changes we need to minimize,
			 * because the alternative is having a window up front
			 * that has no focus and therefore gets no events, and
			 * the user (rightfully) will have no idea why.
			 * -flibit
			 */
			if (OSVersion.Equals("Windows"))
			{
				SDL.SDL_SetHint(
					SDL.SDL_HINT_VIDEO_MINIMIZE_ON_FOCUS_LOSS,
					"1"
				);
			}


			// Set any hints to match XNA4 behavior...
			string hint = SDL.SDL_GetHint(SDL.SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS);
			if (String.IsNullOrEmpty(hint))
			{
				SDL.SDL_SetHint(
					SDL.SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS,
					"1"
				);
			}

			SDL.SDL_SetHint(
				SDL.SDL_HINT_ORIENTATIONS,
				"LandscapeLeft LandscapeRight Portrait"
			);

			// We want to initialize the controllers ASAP!
			SDL.SDL_Event[] evt = new SDL.SDL_Event[1];
			SDL.SDL_PumpEvents();
			while (SDL.SDL_PeepEvents(
				evt,
				1,
				SDL.SDL_EventAction.SDL_GETEVENT,
				(uint) SDL.SDL_EventType.SDL_EVENT_GAMEPAD_ADDED,
				(uint) SDL.SDL_EventType.SDL_EVENT_GAMEPAD_ADDED
			) == 1) {
				INTERNAL_AddInstance(evt[0].gdevice.which);
			}

			if (	OSVersion.Equals("Windows") &&
				SDL.SDL_GetHint("FNA_WIN32_IGNORE_WM_PAINT") != "1" )
			{
				/* Windows has terrible event pumping and doesn't give us
				 * WM_PAINT events correctly. So we get to do this!
				 * -flibit
				 */
				IntPtr prevUserData;
				SDL.SDL_GetEventFilter(
					out prevEventFilter,
					out prevUserData
				);
				SDL.SDL_SetEventFilter(
					win32OnPaint,
					prevUserData
				);
			}

			return titleLocation;
		}

		public static void ProgramExit(object sender, EventArgs e)
		{
			// This _should_ be the last SDL call we make...
			SDL.SDL_QuitSubSystem(
				SDL.SDL_InitFlags.SDL_INIT_VIDEO |
				SDL.SDL_InitFlags.SDL_INIT_GAMEPAD
			);
		}

		#endregion

		#region Allocator

		public static IntPtr Malloc(int size)
		{
			return SDL.SDL_malloc((UIntPtr) size);
		}

		#endregion

		#region Environment

		public static void SetEnv(string name, string value)
		{
			SDL.SDL_SetHintWithPriority(name, value, SDL.SDL_HintPriority.SDL_HINT_OVERRIDE);
		}

		#endregion

		#region Window Methods

		public static GameWindow CreateWindow()
		{
			// Set and initialize the SDL3 window
			SDL.SDL_WindowFlags initFlags = (
				SDL.SDL_WindowFlags.SDL_WINDOW_HIDDEN |
				SDL.SDL_WindowFlags.SDL_WINDOW_INPUT_FOCUS |
				SDL.SDL_WindowFlags.SDL_WINDOW_MOUSE_FOCUS
			) | (SDL.SDL_WindowFlags) FNA3D.FNA3D_PrepareWindowAttributes();

			if ((initFlags & SDL.SDL_WindowFlags.SDL_WINDOW_VULKAN) == SDL.SDL_WindowFlags.SDL_WINDOW_VULKAN)
			{
				string cachePath = SDL.SDL_GetHint(
					"FNA3D_VULKAN_PIPELINE_CACHE_FILE_NAME"
				);
				if (cachePath == null) // Empty is a valid value
				{
					if (	OSVersion.Equals("Windows") ||
						OSVersion.Equals("macOS") ||
						OSVersion.Equals("Linux") ||
						OSVersion.Equals("FreeBSD") ||
						OSVersion.Equals("OpenBSD") ||
						OSVersion.Equals("NetBSD")	)
					{
#if DEBUG // Save pipeline cache files to the base directory for debug builds
						cachePath = "FNA3D_Vulkan_PipelineCache.blob";
#else
						string exeName = Path.GetFileNameWithoutExtension(
							AppDomain.CurrentDomain.FriendlyName
						).Replace(".vshost", "");
						cachePath = Path.Combine(
							SDL.SDL_GetPrefPath(null, "FNA3D"),
							exeName + "_Vulkan_PipelineCache.blob"
						);
#endif
					}
					else
					{
						/* For all non-desktop targets, disable
						 * the pipeline cache. There is usually
						 * some specialized path you have to
						 * take to use pipeline cache files, so
						 * developers will have to do things the
						 * hard way over there.
						 */
						cachePath = string.Empty;
					}
					SDL.SDL_SetHint(
						"FNA3D_VULKAN_PIPELINE_CACHE_FILE_NAME",
						cachePath
					);
				}
			}

			if (Environment.GetEnvironmentVariable("FNA_GRAPHICS_ENABLE_HIGHDPI") == "1")
			{
				initFlags |= SDL.SDL_WindowFlags.SDL_WINDOW_HIGH_PIXEL_DENSITY;
			}

			string title = MonoGame.Utilities.AssemblyHelper.GetDefaultWindowTitle();
			IntPtr window = SDL.SDL_CreateWindow(
				title,
				GraphicsDeviceManager.DefaultBackBufferWidth,
				GraphicsDeviceManager.DefaultBackBufferHeight,
				initFlags
			);
			if (window == IntPtr.Zero)
			{
				/* If this happens, the GL attributes were
				 * rejected by the platform. This is EXTREMELY
				 * rare (unless you're on Android, of course).
				 */
				throw new NoSuitableGraphicsDeviceException(
					SDL.SDL_GetError()
				);
			}
			INTERNAL_SetIcon(window, title);

			// Disable the screensaver.
			SDL.SDL_DisableScreenSaver();

			// We hide the mouse cursor by default.
			OnIsMouseVisibleChanged(false);

			/* If high DPI is not found, unset the HIGHDPI var.
			 * This is our way to communicate that it failed...
			 * -flibit
			 */
			initFlags = (SDL.SDL_WindowFlags) SDL.SDL_GetWindowFlags(window);
			if ((initFlags & SDL.SDL_WindowFlags.SDL_WINDOW_HIGH_PIXEL_DENSITY) == 0)
			{
				Environment.SetEnvironmentVariable("FNA_GRAPHICS_ENABLE_HIGHDPI", "0");
			}

			return new FNAWindow(
				window,
				@"\\.\DISPLAY" + (
					SDL.SDL_GetDisplayForWindow(window)
				).ToString()
			);
		}

		public static void DisposeWindow(GameWindow window)
		{
			/* Some window managers might try to minimize the window as we're
			 * destroying it. This looks pretty stupid and could cause problems,
			 * so set this hint right before we destroy everything.
			 * -flibit
			 */
			SDL.SDL_SetHintWithPriority(
				SDL.SDL_HINT_VIDEO_MINIMIZE_ON_FOCUS_LOSS,
				"0",
				SDL.SDL_HintPriority.SDL_HINT_OVERRIDE
			);

			if (Mouse.WindowHandle == window.Handle)
			{
				Mouse.WindowHandle = IntPtr.Zero;
			}

			if (TouchPanel.WindowHandle == window.Handle)
			{
				TouchPanel.WindowHandle = IntPtr.Zero;
			}

			if (TextInputEXT.WindowHandle == window.Handle)
			{
				TextInputEXT.WindowHandle = IntPtr.Zero;
			}

			SDL.SDL_DestroyWindow(window.Handle);
		}

		public static void ApplyWindowChanges(
			IntPtr window,
			int clientWidth,
			int clientHeight,
			bool wantsFullscreen,
			string screenDeviceName,
			ref string resultDeviceName
		) {
			bool center = false;

			/* The drawable size is now the primary width/height, so
			 * the window needs to accommodate the GL viewport.
			 * -flibit
			 */
			ScaleForWindow(window, false, ref clientWidth, ref clientHeight);

			// When windowed, set the size before moving
			if (!wantsFullscreen)
			{
				bool resize = false;
				if ((SDL.SDL_GetWindowFlags(window) & SDL.SDL_WindowFlags.SDL_WINDOW_FULLSCREEN) != 0)
				{
					SDL.SDL_SetWindowFullscreen(window, false);
					resize = true;
				}
				else
				{
					int w, h;
					SDL.SDL_GetWindowSize(
						window,
						out w,
						out h
					);
					resize = (clientWidth != w || clientHeight != h);
				}
				if (resize)
				{
					SDL.SDL_RestoreWindow(window);
					SDL.SDL_SetWindowSize(window, clientWidth, clientHeight);
					center = true;
				}
			}

			// Get on the right display!
			int displayIndex = 0;
			for (int i = 0; i < GraphicsAdapter.Adapters.Count; i += 1)
			{
				if (screenDeviceName == GraphicsAdapter.Adapters[i].DeviceName)
				{
					displayIndex = i;
					break;
				}
			}

			// Just to be sure, become a window first before changing displays
			if (resultDeviceName != screenDeviceName)
			{
				SDL.SDL_SetWindowFullscreen(window, false);
				resultDeviceName = screenDeviceName;
				center = true;
			}

			// Window always gets centered on changes, per XNA behavior
			if (center)
			{
				// FIXME CSHARP: SDL_WINDOWPOS_CENTERED_DISPLAY
				int pos = (int) (0x2FFF0000 | displayIds[displayIndex]);
				SDL.SDL_SetWindowPosition(
					window,
					pos,
					pos
				);
			}

			// Set fullscreen after we've done all the ugly stuff.
			if (wantsFullscreen)
			{
				if ((SDL.SDL_GetWindowFlags(window) & SDL.SDL_WindowFlags.SDL_WINDOW_HIDDEN) != 0)
				{
					/* If we're still hidden, we can't actually go fullscreen yet.
					 * But, we can at least set the hidden window size to match
					 * what the window/drawable sizes will eventually be later.
					 * -flibit
					 */
					SDL.SDL_DisplayMode* mode = (SDL.SDL_DisplayMode*) SDL.SDL_GetCurrentDisplayMode(
						SDL.SDL_GetDisplayForWindow(window)
					);
					SDL.SDL_SetWindowSize(window, mode->w, mode->h);
				}
				SDL.SDL_SetWindowFullscreen(
					window,
					true
				);
			}

			// Update the mouse window bounds
			if (Mouse.WindowHandle == window)
			{
				Rectangle b = GetWindowBounds(window);
				Mouse.INTERNAL_WindowWidth = b.Width;
				Mouse.INTERNAL_WindowHeight = b.Height;
			}
		}

		public static void ScaleForWindow(IntPtr window, bool invert, ref int w, ref int h)
		{
			int ww, wh, dw, dh;
			SDL.SDL_GetWindowSize(window, out ww, out wh);
			FNA3D.FNA3D_GetDrawableSize(window, out dw, out dh);
			if (	ww != 0 &&
				wh != 0 &&
				dw != 0 &&
				dh != 0 &&
				(ww != dw || wh != dh)	)
			{
				if (invert)
				{
					w = (int) (w * ((float) dw / (float) ww));
					h = (int) (h * ((float) dh / (float) wh));
				}
				else
				{
					w = (int) (w / ((float) dw / (float) ww));
					h = (int) (h / ((float) dh / (float) wh));
				}
			}
		}

		public static Rectangle GetWindowBounds(IntPtr window)
		{
			Rectangle result;
			if ((SDL.SDL_GetWindowFlags(window) & SDL.SDL_WindowFlags.SDL_WINDOW_FULLSCREEN) != 0)
			{
				/* It's easier/safer to just use the display mode here */
				SDL.SDL_DisplayMode* mode = (SDL.SDL_DisplayMode*) SDL.SDL_GetCurrentDisplayMode(
					SDL.SDL_GetDisplayForWindow(window)
				);
				result.X = 0;
				result.Y = 0;
				result.Width = mode->w;
				result.Height = mode->h;
			}
			else
			{
				SDL.SDL_GetWindowPosition(
					window,
					out result.X,
					out result.Y
				);
				SDL.SDL_GetWindowSize(
					window,
					out result.Width,
					out result.Height
				);
			}
			return result;
		}

		public static bool GetWindowResizable(IntPtr window)
		{
			return ((SDL.SDL_GetWindowFlags(window) & SDL.SDL_WindowFlags.SDL_WINDOW_RESIZABLE) != 0);
		}

		public static void SetWindowResizable(IntPtr window, bool resizable)
		{
			SDL.SDL_SetWindowResizable(
				window,
				resizable
			);
		}

		public static bool GetWindowBorderless(IntPtr window)
		{
			return ((SDL.SDL_GetWindowFlags(window) & SDL.SDL_WindowFlags.SDL_WINDOW_BORDERLESS) != 0);
		}

		public static void SetWindowBorderless(IntPtr window, bool borderless)
		{
			SDL.SDL_SetWindowBordered(
				window,
				!borderless
			);
		}

		public static void SetWindowTitle(IntPtr window, string title)
		{
			SDL.SDL_SetWindowTitle(
				window,
				title
			);
		}

		public static bool IsScreenKeyboardShown(IntPtr window)
		{
			return SDL.SDL_ScreenKeyboardShown(window);
		}

		private static void INTERNAL_SetIcon(IntPtr window, string title)
		{
			string fileIn = String.Empty;
			try
			{
				fileIn = INTERNAL_GetIconName(title + ".png");
				if (!String.IsNullOrEmpty(fileIn))
				{
					int w, h, len;
					IntPtr pixels, icon;
					using (Stream stream = TitleContainer.OpenStream(fileIn))
					{
						pixels = FNA3D.ReadImageStream(
							stream,
							out w,
							out h,
							out len
						);
						icon = SDL.SDL_CreateSurfaceFrom(
							w,
							h,
							SDL.SDL_PixelFormat.SDL_PIXELFORMAT_ABGR8888,
							pixels,
							w * 4
						);
					}
					SDL.SDL_SetWindowIcon(window, icon);
					SDL.SDL_DestroySurface(icon);
					FNA3D.FNA3D_Image_Free(pixels);
					return;
				}
			}
			catch(DllNotFoundException)
			{
				// Not that big a deal guys.
			}

			fileIn = INTERNAL_GetIconName(title + ".bmp");
			if (!String.IsNullOrEmpty(fileIn))
			{
				IntPtr icon = SDL.SDL_LoadBMP(fileIn);
				SDL.SDL_SetWindowIcon(window, icon);
				SDL.SDL_DestroySurface(icon);
			}
		}

		private static string INTERNAL_GetIconName(string title)
		{
			string fileIn = Path.Combine(TitleLocation.Path, title);
			if (File.Exists(fileIn))
			{
				// If the title and filename work, it just works. Fine.
				return fileIn;
			}
			else
			{
				// But sometimes the title has invalid characters inside.
				fileIn = Path.Combine(
					TitleLocation.Path,
					INTERNAL_StripBadChars(title)
				);
				if (File.Exists(fileIn))
				{
					return fileIn;
				}
			}
			return String.Empty;
		}

		private static string INTERNAL_StripBadChars(string path)
		{
			/* In addition to the filesystem's invalid charset, we need to
			 * blacklist the Windows standard set too, no matter what.
			 * -flibit
			 */
			char[] hardCodeBadChars = new char[]
			{
				'<',
				'>',
				':',
				'"',
				'/',
				'\\',
				'|',
				'?',
				'*'
			};
			List<char> badChars = new List<char>();
			badChars.AddRange(Path.GetInvalidFileNameChars());
			badChars.AddRange(hardCodeBadChars);

			string stripChars = path;
			foreach (char c in badChars)
			{
				stripChars = stripChars.Replace(c.ToString(), "");
			}
			return stripChars;
		}

		public static void SetTextInputRectangle(IntPtr window, Rectangle rectangle)
		{
			SDL.SDL_Rect rect = new SDL.SDL_Rect();
			rect.x = rectangle.X;
			rect.y = rectangle.Y;
			rect.w = rectangle.Width;
			rect.h = rectangle.Height;
			// FIXME SDL3: Do we need a cursor here?
			SDL.SDL_SetTextInputArea(window, ref rect, 0);
		}

		#endregion

		#region Display Methods

		private static DisplayOrientation INTERNAL_ConvertOrientation(SDL.SDL_DisplayOrientation orientation)
		{
			switch (orientation)
			{
				case SDL.SDL_DisplayOrientation.SDL_ORIENTATION_LANDSCAPE:
					return DisplayOrientation.LandscapeLeft;

				case SDL.SDL_DisplayOrientation.SDL_ORIENTATION_LANDSCAPE_FLIPPED:
					return DisplayOrientation.LandscapeRight;

				case SDL.SDL_DisplayOrientation.SDL_ORIENTATION_PORTRAIT:
				case SDL.SDL_DisplayOrientation.SDL_ORIENTATION_PORTRAIT_FLIPPED:
					return DisplayOrientation.Portrait;

				default:
					throw new NotSupportedException("FNA does not support this device orientation.");
			}
		}

		private static void INTERNAL_HandleOrientationChange(
			DisplayOrientation orientation,
			GraphicsDevice graphicsDevice,
			GraphicsAdapter graphicsAdapter,
			FNAWindow window
		) {
			// Flip the backbuffer dimensions if needed
			int width = graphicsDevice.PresentationParameters.BackBufferWidth;
			int height = graphicsDevice.PresentationParameters.BackBufferHeight;
			int min = Math.Min(width, height);
			int max = Math.Max(width, height);

			if (orientation == DisplayOrientation.Portrait)
			{
				graphicsDevice.PresentationParameters.BackBufferWidth = min;
				graphicsDevice.PresentationParameters.BackBufferHeight = max;
			}
			else
			{
				graphicsDevice.PresentationParameters.BackBufferWidth = max;
				graphicsDevice.PresentationParameters.BackBufferHeight = min;
			}

			// Update the graphics device and window
			graphicsDevice.PresentationParameters.DisplayOrientation = orientation;
			window.CurrentOrientation = orientation;

			graphicsDevice.Reset(
				graphicsDevice.PresentationParameters,
				graphicsAdapter
			);
			window.INTERNAL_OnOrientationChanged();
		}

		public static bool SupportsOrientationChanges()
		{
			return SupportsOrientations;
		}

		#endregion

		#region Event Loop

		public static GraphicsAdapter RegisterGame(Game game)
		{
			SDL.SDL_ShowWindow(game.Window.Handle);

			// Store this for internal event filter work
			activeGames.Add(game);

			return FetchDisplayAdapter(game.Window.Handle);
		}

		public static void UnregisterGame(Game game)
		{
			activeGames.Remove(game);
		}

		public static unsafe void PollEvents(
			Game game,
			ref GraphicsAdapter currentAdapter,
			bool[] textInputControlDown,
			ref bool textInputSuppress
		) {
			SDL.SDL_Event evt;
			char* charsBuffer = stackalloc char[32]; // SDL_TEXTINPUTEVENT_TEXT_SIZE
			while (SDL.SDL_PollEvent(out evt))
			{
				// Keyboard
				if (evt.type == (uint) SDL.SDL_EventType.SDL_EVENT_KEY_DOWN)
				{
					Keys key = ToXNAKey(ref evt.key.key, ref evt.key.scancode);
					if (!Keyboard.keys.Contains(key))
					{
						Keyboard.keys.Add(key);
						int textIndex;
						if (FNAPlatform.TextInputBindings.TryGetValue(key, out textIndex))
						{
							textInputControlDown[textIndex] = true;
							TextInputEXT.OnTextInput(FNAPlatform.TextInputCharacters[textIndex]);
						}
						else if ((Keyboard.keys.Contains(Keys.LeftControl) || Keyboard.keys.Contains(Keys.RightControl))
							&& key == Keys.V)
						{
							textInputControlDown[6] = true;
							TextInputEXT.OnTextInput(FNAPlatform.TextInputCharacters[6]);
							textInputSuppress = true;
						}
					}
					else if (evt.key.repeat)
					{
						int textIndex;
						if (FNAPlatform.TextInputBindings.TryGetValue(key, out textIndex))
						{
							TextInputEXT.OnTextInput(FNAPlatform.TextInputCharacters[textIndex]);
						}
						else if ((Keyboard.keys.Contains(Keys.LeftControl) || Keyboard.keys.Contains(Keys.RightControl))
							&& key == Keys.V)
						{
							TextInputEXT.OnTextInput(FNAPlatform.TextInputCharacters[6]);
						}
					}
				}
				else if (evt.type == (uint) SDL.SDL_EventType.SDL_EVENT_KEY_UP)
				{
					Keys key = ToXNAKey(ref evt.key.key, ref evt.key.scancode);
					if (Keyboard.keys.Remove(key))
					{
						int value;
						if (FNAPlatform.TextInputBindings.TryGetValue(key, out value))
						{
							textInputControlDown[value] = false;
						}
						else if (((!Keyboard.keys.Contains(Keys.LeftControl) && !Keyboard.keys.Contains(Keys.RightControl)) && textInputControlDown[6])
							|| key == Keys.V)
						{
							textInputControlDown[6] = false;
							textInputSuppress = false;
						}
					}
				}

				// Mouse Input
				else if (evt.type == (uint) SDL.SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN)
				{
					Mouse.INTERNAL_onClicked(evt.button.button - 1);
				}
				else if (evt.type == (uint) SDL.SDL_EventType.SDL_EVENT_MOUSE_WHEEL)
				{
					// FIXME SDL3: Should this be rounded?
					// 120 units per notch. Because reasons.
					Mouse.INTERNAL_MouseWheel += (int) evt.wheel.y * 120;
				}

				// Touch Input
				else if (evt.type == (uint) SDL.SDL_EventType.SDL_EVENT_FINGER_DOWN)
				{
					// Windows only notices a touch screen once it's touched
					TouchPanel.TouchDeviceExists = true;

					TouchPanel.INTERNAL_onTouchEvent(
						(int) evt.tfinger.fingerID,
						TouchLocationState.Pressed,
						evt.tfinger.x,
						evt.tfinger.y,
						0,
						0
					);
				}
				else if (evt.type == (uint) SDL.SDL_EventType.SDL_EVENT_FINGER_MOTION)
				{
					TouchPanel.INTERNAL_onTouchEvent(
						(int) evt.tfinger.fingerID,
						TouchLocationState.Moved,
						evt.tfinger.x,
						evt.tfinger.y,
						evt.tfinger.dx,
						evt.tfinger.dy
					);
				}
				else if (evt.type == (uint) SDL.SDL_EventType.SDL_EVENT_FINGER_UP || evt.type == (uint) SDL.SDL_EventType.SDL_EVENT_FINGER_CANCELED)
				{
					TouchPanel.INTERNAL_onTouchEvent(
						(int) evt.tfinger.fingerID,
						TouchLocationState.Released,
						evt.tfinger.x,
						evt.tfinger.y,
						0,
						0
					);
				}

				// Various Window Events...
				else if (evt.type >= (uint) SDL.SDL_EventType.SDL_EVENT_WINDOW_FIRST && evt.type <= (uint) SDL.SDL_EventType.SDL_EVENT_WINDOW_LAST)
				{
					// Window Focus
					if (evt.type == (uint) SDL.SDL_EventType.SDL_EVENT_WINDOW_FOCUS_GAINED)
					{
						game.IsActive = true;

						if (SDL.SDL_GetCurrentVideoDriver() == "x11")
						{
							// If we alt-tab away, we lose the 'fullscreen desktop' flag on some WMs
							SDL.SDL_SetWindowFullscreen(
								game.Window.Handle,
								game.GraphicsDevice.PresentationParameters.IsFullScreen
							);
						}

						// Disable the screensaver when we're back.
						SDL.SDL_DisableScreenSaver();
					}
					else if (evt.type == (uint) SDL.SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST)
					{
						game.IsActive = false;

						if (SDL.SDL_GetCurrentVideoDriver() == "x11")
						{
							SDL.SDL_SetWindowFullscreen(game.Window.Handle, false);
						}

						// Give the screensaver back, we're not that important now.
						SDL.SDL_EnableScreenSaver();
					}

					// Window Resize
					else if (evt.type == (uint) SDL.SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED)
					{
						/* This is called on both API and WM resizes.
						 * We have to use WindowBounds instead of data1/2,
						 * since data1/2 are in pixels, not desktop units.
						 */
						Rectangle b = GetWindowBounds(Mouse.WindowHandle);
						Mouse.INTERNAL_WindowWidth = b.Width;
						Mouse.INTERNAL_WindowHeight = b.Height;
					}
					else if (evt.type == (uint) SDL.SDL_EventType.SDL_EVENT_WINDOW_RESIZED)
					{
						/* This should be called on user resize only, NOT ApplyChanges!
						 * Sadly some window managers are idiots and fire events anyway.
						 * Also ignore any other "resizes" (alt-tab, fullscreen, etc.)
						 * -flibit
						 */
						SDL.SDL_WindowFlags flags = SDL.SDL_GetWindowFlags(game.Window.Handle);
						if (	(flags & SDL.SDL_WindowFlags.SDL_WINDOW_RESIZABLE) != 0 &&
							(flags & (SDL.SDL_WindowFlags.SDL_WINDOW_INPUT_FOCUS | SDL.SDL_WindowFlags.SDL_WINDOW_MOUSE_FOCUS)) != 0	)
						{
							((FNAWindow) game.Window).INTERNAL_ClientSizeChanged();
						}
					}
					else if (evt.type == (uint) SDL.SDL_EventType.SDL_EVENT_WINDOW_EXPOSED)
					{
						// This is typically called when the window is made bigger
						game.RedrawWindow();
					}

					// Window Move
					else if (evt.type == (uint) SDL.SDL_EventType.SDL_EVENT_WINDOW_MOVED)
					{
						/* Apparently if you move the window to a new
						 * display, a GraphicsDevice Reset occurs.
						 * -flibit
						 */
						GraphicsAdapter next = FetchDisplayAdapter(game.Window.Handle);

						if (next != currentAdapter)
						{
							currentAdapter = next;
							game.GraphicsDevice.Reset(
								game.GraphicsDevice.PresentationParameters,
								currentAdapter
							);
						}
					}

					// Mouse Focus
					else if (evt.type == (uint) SDL.SDL_EventType.SDL_EVENT_WINDOW_MOUSE_ENTER)
					{
						SDL.SDL_DisableScreenSaver();
					}
					else if (evt.type == (uint) SDL.SDL_EventType.SDL_EVENT_WINDOW_MOUSE_LEAVE)
					{
						SDL.SDL_EnableScreenSaver();
					}

					// Full screen
					else if (evt.type == (uint) SDL.SDL_EventType.SDL_EVENT_WINDOW_ENTER_FULLSCREEN)
					{
						Object manager = game.Services.GetService(typeof(IGraphicsDeviceManager));
						if (manager != null && manager is GraphicsDeviceManager)
						{
							((GraphicsDeviceManager) manager).IsFullScreen = true;
						}
					}
					else if (evt.type == (uint) SDL.SDL_EventType.SDL_EVENT_WINDOW_LEAVE_FULLSCREEN)
					{
						Object manager = game.Services.GetService(typeof(IGraphicsDeviceManager));
						if (manager != null && manager is GraphicsDeviceManager)
						{
							((GraphicsDeviceManager) manager).IsFullScreen = false;
						}
					}
				}

				// Display Events
				else if (evt.type >= (uint) SDL.SDL_EventType.SDL_EVENT_DISPLAY_FIRST && evt.type <= (uint) SDL.SDL_EventType.SDL_EVENT_DISPLAY_LAST)
				{
					GraphicsAdapter.AdaptersChanged();

					currentAdapter = FetchDisplayAdapter(game.Window.Handle);

					// Orientation Change
					if (evt.type == (uint) SDL.SDL_EventType.SDL_EVENT_DISPLAY_ORIENTATION)
					{
						if (SupportsOrientationChanges())
						{
							DisplayOrientation orientation = INTERNAL_ConvertOrientation(
								(SDL.SDL_DisplayOrientation) evt.display.data1
							);

							INTERNAL_HandleOrientationChange(
								orientation,
								game.GraphicsDevice,
								currentAdapter,
								(FNAWindow) game.Window
							);
						}
					}
					else
					{
						// Quietly update, this is probably a hotplug
						game.GraphicsDevice.QuietlyUpdateAdapter(
							currentAdapter
						);
					}
				}

				// Controller device management
				else if (evt.type == (uint) SDL.SDL_EventType.SDL_EVENT_GAMEPAD_ADDED)
				{
					INTERNAL_AddInstance(evt.gdevice.which);
				}
				else if (evt.type == (uint) SDL.SDL_EventType.SDL_EVENT_GAMEPAD_REMOVED)
				{
					INTERNAL_RemoveInstance(evt.gdevice.which);
				}

				// Text Input
				else if (evt.type == (uint) SDL.SDL_EventType.SDL_EVENT_TEXT_INPUT && !textInputSuppress)
				{
					// Based on the SDL2# LPUtf8StrMarshaler
					int bytes = MeasureStringLength(evt.text.text);
					if (bytes > 0)
					{
						/* UTF8 will never encode more characters
						 * than bytes in a string, so bytes is a
						 * suitable upper estimate of size needed
						 */
						int chars = Encoding.UTF8.GetChars(
							(byte*) evt.text.text,
							bytes,
							charsBuffer,
							bytes
						);

						for (int i = 0; i < chars; i += 1)
						{
							TextInputEXT.OnTextInput(charsBuffer[i]);
						}
					}
				}

				else if (evt.type == (uint) SDL.SDL_EventType.SDL_EVENT_TEXT_EDITING)
				{
					int bytes = MeasureStringLength(evt.edit.text);
					if (bytes > 0)
					{
						int chars = Encoding.UTF8.GetChars(
							(byte*) evt.edit.text,
							bytes,
							charsBuffer,
							bytes
						);
						string text = new string(charsBuffer, 0, chars);
						TextInputEXT.OnTextEditing(text, evt.edit.start, evt.edit.length);
					}
					else
					{
						TextInputEXT.OnTextEditing(null, 0, 0);
					}
				}

				// Quit
				else if (evt.type == (uint) SDL.SDL_EventType.SDL_EVENT_QUIT)
				{
					game.RunApplication = false;
					break;
				}
			}
		}

		private unsafe static int MeasureStringLength(byte* ptr)
		{
			int bytes;
			for (bytes = 0; *ptr != 0; ptr += 1, bytes += 1);
			return bytes;
		}

		public static bool NeedsPlatformMainLoop()
		{
			return SDL.SDL_GetPlatform().Equals("Emscripten");
		}

		public static void RunPlatformMainLoop(Game game)
		{
			if (SDL.SDL_GetPlatform().Equals("Emscripten"))
			{
				emscriptenGame = game;
				emscripten_set_main_loop(
					RunEmscriptenMainLoop,
					0,
					1
				);
			}
			else
			{
				throw new NotSupportedException(
					"Cannot run the main loop of an unknown platform"
				);
			}
		}

		#endregion

		#region Emscripten Main Loop

		private static Game emscriptenGame;
		private delegate void em_callback_func();

		[DllImport("__Native", CallingConvention = CallingConvention.Cdecl)]
		private static extern void emscripten_set_main_loop(
			em_callback_func func,
			int fps,
			int simulate_infinite_loop
		);

		[DllImport("__Native", CallingConvention = CallingConvention.Cdecl)]
		private static extern void emscripten_cancel_main_loop();

		[ObjCRuntime.MonoPInvokeCallback(typeof(em_callback_func))]
		private static void RunEmscriptenMainLoop()
		{
			emscriptenGame.RunOneFrame();

			// FIXME: Is this even needed...?
			if (!emscriptenGame.RunApplication)
			{
				emscriptenGame.Exit();
				emscripten_cancel_main_loop();
			}
		}

		#endregion

		#region Graphics Methods

		// FIXME SDL3: This is really sloppy -flibit
		private static uint[] displayIds;
		private static GraphicsAdapter FetchDisplayAdapter(IntPtr window, bool retry = true)
		{
			uint displayId = SDL.SDL_GetDisplayForWindow(window);

			int index = -1;
			for (int i = 0; i < displayIds.Length; i += 1)
			{
				if (displayId == displayIds[i])
				{
					index = i;
					break;
				}
			}

			if (index < 0 || index > GraphicsAdapter.Adapters.Count)
			{
				FNALoggerEXT.LogWarn("SDL3 Window ID and Display ID desync'd");
				if (retry)
				{
					GraphicsAdapter.AdaptersChanged();
					return FetchDisplayAdapter(window, false);
				}
				FNALoggerEXT.LogWarn("SDL3 Window ID and Display ID desync'd really badly");
				return GraphicsAdapter.DefaultAdapter;
			}
			return GraphicsAdapter.Adapters[index];
		}

		public static GraphicsAdapter[] GetGraphicsAdapters()
		{
			int numDisplays;
			uint* displays = (uint*) SDL.SDL_GetDisplays(out numDisplays);
			GraphicsAdapter[] adapters = new GraphicsAdapter[numDisplays];
			displayIds = new uint[numDisplays];
			for (int i = 0; i < adapters.Length; i += 1)
			{
				List<DisplayMode> modes = new List<DisplayMode>();
				int numModes;
				SDL.SDL_DisplayMode** displayModes = (SDL.SDL_DisplayMode**) SDL.SDL_GetFullscreenDisplayModes(displays[i], out numModes);
				for (int j = numModes - 1; j >= 0; j -= 1)
				{
					// Check for dupes caused by varying refresh rates.
					bool dupe = false;
					foreach (DisplayMode mode in modes)
					{
						if (displayModes[j]->w == mode.Width && displayModes[j]->h == mode.Height)
						{
							dupe = true;
						}
					}
					if (!dupe)
					{
						modes.Add(
							new DisplayMode(
								displayModes[j]->w,
								displayModes[j]->h,
								SurfaceFormat.Color // FIXME: Assumption!
							)
						);
					}
				}
				SDL.SDL_free((IntPtr) displayModes);
				adapters[i] = new GraphicsAdapter(
					new DisplayModeCollection(modes),
					@"\\.\DISPLAY" + (i + 1).ToString(),
					SDL.SDL_GetDisplayName(displays[i])
				);
				displayIds[i] = displays[i];
			}
			SDL.SDL_free((IntPtr) displays);
			return adapters;
		}

		public static DisplayMode GetCurrentDisplayMode(int adapterIndex)
		{
			SDL.SDL_DisplayMode *mode = (SDL.SDL_DisplayMode*) SDL.SDL_GetCurrentDisplayMode(displayIds[adapterIndex]);

			// FIXME: iOS needs to factor in the DPI!

			return new DisplayMode(
				mode->w,
				mode->h,
				SurfaceFormat.Color // FIXME: Assumption!
			);
		}

		#endregion

		#region Mouse Methods

		public static void GetMouseState(
			IntPtr window,
			out int x,
			out int y,
			out ButtonState left,
			out ButtonState middle,
			out ButtonState right,
			out ButtonState x1,
			out ButtonState x2
		) {
			SDL.SDL_MouseButtonFlags flags;
			float fx, fy;
			if (GetRelativeMouseMode(window))
			{
				flags = SDL.SDL_GetRelativeMouseState(out fx, out fy);
			}
			else if (SupportsGlobalMouse)
			{
				flags = SDL.SDL_GetGlobalMouseState(out fx, out fy);
				int wx = 0, wy = 0;
				SDL.SDL_GetWindowPosition(window, out wx, out wy);
				fx -= wx;
				fy -= wy;
			}
			else
			{
				/* This is inaccurate, but what can you do... */
				flags = SDL.SDL_GetMouseState(out fx, out fy);
			}
			// FIXME SDL3: Should this be rounded?
			x = (int) fx;
			y = (int) fy;
			left =		(ButtonState) (flags & SDL.SDL_MouseButtonFlags.SDL_BUTTON_LMASK);
			middle =	(ButtonState) ((uint) (flags & SDL.SDL_MouseButtonFlags.SDL_BUTTON_MMASK) >> 1);
			right =		(ButtonState) ((uint) (flags & SDL.SDL_MouseButtonFlags.SDL_BUTTON_RMASK) >> 2);
			x1 =		(ButtonState) ((uint) (flags & SDL.SDL_MouseButtonFlags.SDL_BUTTON_X1MASK) >> 3);
			x2 =		(ButtonState) ((uint) (flags & SDL.SDL_MouseButtonFlags.SDL_BUTTON_X2MASK) >> 4);
		}

		public static void WarpMouseInWindow(IntPtr window, int x, int y)
		{
			// Implicit conversion to float
			SDL.SDL_WarpMouseInWindow(window, x, y);
		}

		public static void OnIsMouseVisibleChanged(bool visible)
		{
			if (visible)
			{
				SDL.SDL_ShowCursor();
			}
			else
			{
				SDL.SDL_HideCursor();
			}
		}

		public static bool GetRelativeMouseMode(IntPtr window)
		{
			return SDL.SDL_GetWindowRelativeMouseMode(window);
		}

		public static void SetRelativeMouseMode(IntPtr window, bool enable)
		{
			SDL.SDL_SetWindowRelativeMouseMode(window, enable);
			if (enable)
			{
			    // Flush this value, it's going to be jittery
			    float filler;
			    SDL.SDL_GetRelativeMouseState(out filler, out filler);
			}
		}

		#endregion

		#region Storage Methods

		private static string GetBaseDirectory()
		{
			if (Environment.GetEnvironmentVariable("FNA_SDL_FORCE_BASE_PATH") != "1")
			{
				// If your platform uses a CLR, you want to be in this list!
				if (	OSVersion.Equals("Windows") ||
					OSVersion.Equals("macOS") ||
					OSVersion.Equals("Linux") ||
					OSVersion.Equals("FreeBSD") ||
					OSVersion.Equals("OpenBSD") ||
					OSVersion.Equals("NetBSD")	)
				{
					return AppDomain.CurrentDomain.BaseDirectory;
				}
			}
			string result = SDL.SDL_GetBasePath();
			if (string.IsNullOrEmpty(result))
			{
				result = AppDomain.CurrentDomain.BaseDirectory;
			}
			if (string.IsNullOrEmpty(result))
			{
				/* In the chance that there is no base directory,
				 * return the working directory and hope for the best.
				 *
				 * If we've reached this, the game has either been
				 * started from its directory, or a wrapper has set up
				 * the working directory to the game dir for us.
				 *
				 * Note about Android:
				 *
				 * There is no way from the C# side of things to cleanly
				 * obtain where the game is located without looking at an
				 * instance of System.Diagnostics.StackTrace or without
				 * some interop between the Java and C# side of things.
				 * We're assuming that either the environment itself is
				 * setting one of the possible base paths to point to the
				 * game dir, or that the Java side has called into the C#
				 * side to set Environment.CurrentDirectory.
				 *
				 * In the best case, nothing would be set and the game
				 * wouldn't use the title location in the first place, as
				 * the assets would be read directly from the .apk / .obb
				 * -ade
				 */
				result = Environment.CurrentDirectory;
			}
			return result;
		}

		public static string GetStorageRoot()
		{
			// Generate the path of the game's savefolder
			string exeName = Path.GetFileNameWithoutExtension(
				AppDomain.CurrentDomain.FriendlyName
			).Replace(".vshost", "");

			// Get the OS save folder, append the EXE name
			if (OSVersion.Equals("Windows"))
			{
				return Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
					"SavedGames",
					exeName
				);
			}
			if (OSVersion.Equals("macOS"))
			{
				string osConfigDir = Environment.GetEnvironmentVariable("HOME");
				if (String.IsNullOrEmpty(osConfigDir))
				{
					return "."; // Oh well.
				}
				return Path.Combine(
					osConfigDir,
					"Library/Application Support",
					exeName
				);
			}
			if (	OSVersion.Equals("Linux") ||
				OSVersion.Equals("FreeBSD") ||
				OSVersion.Equals("OpenBSD") ||
				OSVersion.Equals("NetBSD")	)
			{
				// Assuming a non-macOS Unix platform will follow the XDG. Which it should.
				string osConfigDir = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
				if (String.IsNullOrEmpty(osConfigDir))
				{
					osConfigDir = Environment.GetEnvironmentVariable("HOME");
					if (String.IsNullOrEmpty(osConfigDir))
					{
						return ".";	// Oh well.
					}
					osConfigDir += "/.local/share";
				}
				return Path.Combine(osConfigDir, exeName);
			}

			/* There is a minor inaccuracy here: SDL_GetPrefPath
			 * creates the directories right away, whereas XNA will
			 * only create the directory upon creating a container.
			 * So if you create a StorageDevice and hit a property,
			 * the game folder is made early!
			 * -flibit
			 */
			return SDL.SDL_GetPrefPath(null, exeName);
		}

		public static DriveInfo GetDriveInfo(string storageRoot)
		{
			DriveInfo result;
			try
			{
				result = new DriveInfo(MonoPathRootWorkaround(storageRoot));
			}
			catch(Exception e)
			{
				FNALoggerEXT.LogError("Failed to get DriveInfo: " + e.ToString());
				result = null;
			}
			return result;
		}

		private static string MonoPathRootWorkaround(string storageRoot)
		{
			if (Environment.OSVersion.Platform == PlatformID.Win32NT)
			{
				// This is what we should be doing everywhere...
				return Path.GetPathRoot(storageRoot);
			}

			// This is stolen from Mono's Path.cs
			if (storageRoot == null)
			{
				return null;
			}
			if (storageRoot.Trim().Length == 0)
			{
				throw new ArgumentException("The specified path is not of a legal form.");
			}
			if (!Path.IsPathRooted(storageRoot) && !storageRoot.Contains(":"))
			{
				return string.Empty;
			}

			/* FIXME: Mono bug!
			 *
			 * For Unix, the Mono Path.GetPathRoot is pretty lazy:
			 * https://github.com/mono/mono/blob/master/mcs/class/corlib/System.IO/Path.cs#L443
			 * It should actually be checking the drives and
			 * comparing them to the provided path.
			 * If a Mono maintainer is reading this, please steal
			 * this code so we don't have to hack around Mono!
			 *
			 * -flibit
			 */
			int drive = -1, length = 0;
			string[] drives = Environment.GetLogicalDrives();
			for (int i = 0; i < drives.Length; i += 1)
			{
				if (string.IsNullOrEmpty(drives[i]))
				{
					// ... What?
					continue;
				}
				string name = drives[i];
				if (name[name.Length - 1] != Path.DirectorySeparatorChar)
				{
					name += Path.DirectorySeparatorChar;
				}
				if (	storageRoot.StartsWith(name) &&
					name.Length > length	)
				{
					drive = i;
					length = name.Length;
				}
			}
			if (drive >= 0)
			{
				return drives[drive];
			}

			// Uhhhhh
			return Path.GetPathRoot(storageRoot);
		}

		public static IntPtr ReadToPointer(string path, out IntPtr size)
		{
			UIntPtr resultSize;
			IntPtr result = SDL.SDL_LoadFile(path, out resultSize);
			size = (IntPtr) resultSize.ToPointer();
			return result;
		}

		public static void FreeFilePointer(IntPtr file)
		{
			SDL.SDL_free(file);
		}

		#endregion

		#region Logging/Messaging Methods

		public static void ShowRuntimeError(string title, string message)
		{
			SDL.SDL_ShowSimpleMessageBox(
				SDL.SDL_MessageBoxFlags.SDL_MESSAGEBOX_ERROR,
				title ?? "",
				message ?? "",
				IntPtr.Zero
			);
		}

		#endregion

		#region Microphone Implementation

		/* Microphone is almost never used, so we give this subsystem
		 * special treatment and init only when we start calling these
		 * functions.
		 * -flibit
		 */
		private static bool micInit = false;

		// FIXME SDL3: This is really sloppy -flibit
		private static Dictionary<uint, IntPtr> micStreams;

		public static Microphone[] GetMicrophones()
		{
			// Init subsystem if needed
			if (!micInit)
			{
				SDL.SDL_InitSubSystem(SDL.SDL_InitFlags.SDL_INIT_AUDIO);
				micStreams = new Dictionary<uint, IntPtr>();
				micInit = true;
			}

			// How many devices do we have...?
			int numDev;
			uint* devices = (uint*) SDL.SDL_GetAudioRecordingDevices(out numDev);
			if (numDev < 1)
			{
				// Blech
				return new Microphone[0];
			}
			Microphone[] result = new Microphone[numDev + 1];

			// Default input format
			SDL.SDL_AudioSpec want = new SDL.SDL_AudioSpec();
			want.freq = Microphone.SAMPLERATE;
			want.format = SDL.SDL_AudioFormat.SDL_AUDIO_S16;
			want.channels = 1;

			// First mic is always OS default
			result[0] = new Microphone(
				SDL.SDL_OpenAudioDevice(
					0xFFFFFFFEu, // FIXME CSHARP: SDL_AUDIO_DEVICE_DEFAULT_RECORDING
					ref want
				),
				"Default Device"
			);
			for (int i = 0; i < numDev; i += 1)
			{
				string name = SDL.SDL_GetAudioDeviceName(devices[i]);
				result[i + 1] = new Microphone(
					SDL.SDL_OpenAudioDevice(
						devices[i],
						ref want
					),
					name
				);

				IntPtr stream;
				SDL.SDL_AudioSpec have;
				int filler;
				SDL.SDL_GetAudioDeviceFormat(devices[i], out have, out filler);
				stream = SDL.SDL_CreateAudioStream(ref want, ref have);

				SDL.SDL_BindAudioStream(devices[i], stream);
				micStreams.Add(devices[i], stream);
			}
			SDL.SDL_free((IntPtr) devices);
			return result;
		}

		public static unsafe int GetMicrophoneSamples(
			uint handle,
			byte[] buffer,
			int offset,
			int count
		) {
			fixed (byte* ptr = &buffer[offset])
			{
				return (int) SDL.SDL_GetAudioStreamData(
					micStreams[handle],
					(IntPtr) ptr,
					count
				);
			}
		}

		public static int GetMicrophoneQueuedBytes(uint handle)
		{
			return SDL.SDL_GetAudioStreamQueued(micStreams[handle]);
		}

		public static void StartMicrophone(uint handle)
		{
			SDL.SDL_ResumeAudioDevice(handle);
		}

		public static void StopMicrophone(uint handle)
		{
			SDL.SDL_PauseAudioDevice(handle);
		}

		#endregion

		#region GamePad Backend

		// Controller device information
		private static IntPtr[] INTERNAL_devices = new IntPtr[GamePad.GAMEPAD_COUNT];
		private static Dictionary<uint, int> INTERNAL_instanceList = new Dictionary<uint, int>();
		private static string[] INTERNAL_guids = GenStringArray();

		// Cached GamePadStates/Capabilities
		private static GamePadState[] INTERNAL_states = new GamePadState[GamePad.GAMEPAD_COUNT];
		private static GamePadCapabilities[] INTERNAL_capabilities = new GamePadCapabilities[GamePad.GAMEPAD_COUNT];

		private static readonly GamePadType[] INTERNAL_gamepadType = new GamePadType[]
		{
			GamePadType.Unknown,
			GamePadType.GamePad,
			GamePadType.Wheel,
			GamePadType.ArcadeStick,
			GamePadType.FlightStick,
			GamePadType.DancePad,
			GamePadType.Guitar,
			GamePadType.DrumKit,
			GamePadType.BigButtonPad
		};

		public static GamePadCapabilities GetGamePadCapabilities(int index)
		{
			if (INTERNAL_devices[index] == IntPtr.Zero)
			{
				return new GamePadCapabilities();
			}
			return INTERNAL_capabilities[index];
		}

		public static GamePadState GetGamePadState(int index, GamePadDeadZone deadZoneMode)
		{
			IntPtr device = INTERNAL_devices[index];
			if (device == IntPtr.Zero)
			{
				return new GamePadState();
			}

			// Sticks
			const float axisDivisor = 32767.0f;
			Vector2 stickLeft = new Vector2(
				SDL.SDL_GetGamepadAxis(device, SDL.SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX) / axisDivisor,
				SDL.SDL_GetGamepadAxis(device, SDL.SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY) / -axisDivisor
			);
			Vector2 stickRight = new Vector2(
				SDL.SDL_GetGamepadAxis(device, SDL.SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTX) / axisDivisor,
				SDL.SDL_GetGamepadAxis(device, SDL.SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTY) / -axisDivisor
			);

			// Triggers
			float triggerLeft = SDL.SDL_GetGamepadAxis(device, SDL.SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFT_TRIGGER) / axisDivisor;
			float triggerRight = SDL.SDL_GetGamepadAxis(device, SDL.SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHT_TRIGGER) / axisDivisor;

			// Buttons
			Buttons gc_buttonState = (Buttons) 0;
			if (SDL.SDL_GetGamepadButton(device, SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH))
			{
				gc_buttonState |= Buttons.A;
			}
			if (SDL.SDL_GetGamepadButton(device, SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST))
			{
				gc_buttonState |= Buttons.B;
			}
			if (SDL.SDL_GetGamepadButton(device, SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST))
			{
				gc_buttonState |= Buttons.X;
			}
			if (SDL.SDL_GetGamepadButton(device, SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH))
			{
				gc_buttonState |= Buttons.Y;
			}
			if (SDL.SDL_GetGamepadButton(device, SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_BACK))
			{
				gc_buttonState |= Buttons.Back;
			}
			if (SDL.SDL_GetGamepadButton(device, SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_GUIDE))
			{
				gc_buttonState |= Buttons.BigButton;
			}
			if (SDL.SDL_GetGamepadButton(device, SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START))
			{
				gc_buttonState |= Buttons.Start;
			}
			if (SDL.SDL_GetGamepadButton(device, SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_STICK))
			{
				gc_buttonState |= Buttons.LeftStick;
			}
			if (SDL.SDL_GetGamepadButton(device, SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_STICK))
			{
				gc_buttonState |= Buttons.RightStick;
			}
			if (SDL.SDL_GetGamepadButton(device, SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER))
			{
				gc_buttonState |= Buttons.LeftShoulder;
			}
			if (SDL.SDL_GetGamepadButton(device, SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER))
			{
				gc_buttonState |= Buttons.RightShoulder;
			}

			// DPad
			ButtonState dpadUp = ButtonState.Released;
			ButtonState dpadDown = ButtonState.Released;
			ButtonState dpadLeft = ButtonState.Released;
			ButtonState dpadRight = ButtonState.Released;
			if (SDL.SDL_GetGamepadButton(device, SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP))
			{
				gc_buttonState |= Buttons.DPadUp;
				dpadUp = ButtonState.Pressed;
			}
			if (SDL.SDL_GetGamepadButton(device, SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN))
			{
				gc_buttonState |= Buttons.DPadDown;
				dpadDown = ButtonState.Pressed;
			}
			if (SDL.SDL_GetGamepadButton(device, SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT))
			{
				gc_buttonState |= Buttons.DPadLeft;
				dpadLeft = ButtonState.Pressed;
			}
			if (SDL.SDL_GetGamepadButton(device, SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT))
			{
				gc_buttonState |= Buttons.DPadRight;
				dpadRight = ButtonState.Pressed;
			}

			// Extensions
			if (SDL.SDL_GetGamepadButton(device, SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_MISC1))
			{
				gc_buttonState |= Buttons.Misc1EXT;
			}
			if (SDL.SDL_GetGamepadButton(device, SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_PADDLE1))
			{
				gc_buttonState |= Buttons.Paddle1EXT;
			}
			if (SDL.SDL_GetGamepadButton(device, SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_PADDLE1))
			{
				gc_buttonState |= Buttons.Paddle2EXT;
			}
			if (SDL.SDL_GetGamepadButton(device, SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_PADDLE2))
			{
				gc_buttonState |= Buttons.Paddle3EXT;
			}
			if (SDL.SDL_GetGamepadButton(device, SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_PADDLE2))
			{
				gc_buttonState |= Buttons.Paddle4EXT;
			}
			if (SDL.SDL_GetGamepadButton(device, SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_TOUCHPAD))
			{
				gc_buttonState |= Buttons.TouchPadEXT;
			}

			// Build the GamePadState, increment PacketNumber if state changed.
			GamePadState gc_builtState = new GamePadState(
				new GamePadThumbSticks(stickLeft, stickRight, deadZoneMode),
				new GamePadTriggers(triggerLeft, triggerRight, deadZoneMode),
				new GamePadButtons(gc_buttonState),
				new GamePadDPad(dpadUp, dpadDown, dpadLeft, dpadRight)
			);
			gc_builtState.IsConnected = true;
			gc_builtState.PacketNumber = INTERNAL_states[index].PacketNumber;
			if (gc_builtState != INTERNAL_states[index])
			{
				gc_builtState.PacketNumber += 1;
				INTERNAL_states[index] = gc_builtState;
			}

			return gc_builtState;
		}

		public static bool SetGamePadVibration(int index, float leftMotor, float rightMotor)
		{
			IntPtr device = INTERNAL_devices[index];
			if (device == IntPtr.Zero)
			{
				return false;
			}

			return SDL.SDL_RumbleGamepad(
				device,
				(ushort) (MathHelper.Clamp(leftMotor, 0.0f, 1.0f) * 0xFFFF),
				(ushort) (MathHelper.Clamp(rightMotor, 0.0f, 1.0f) * 0xFFFF),
				0
			);
		}

		public static bool SetGamePadTriggerVibration(int index, float leftTrigger, float rightTrigger)
		{
			IntPtr device = INTERNAL_devices[index];
			if (device == IntPtr.Zero)
			{
				return false;
			}

			return SDL.SDL_RumbleGamepadTriggers(
				device,
				(ushort) (MathHelper.Clamp(leftTrigger, 0.0f, 1.0f) * 0xFFFF),
				(ushort) (MathHelper.Clamp(rightTrigger, 0.0f, 1.0f) * 0xFFFF),
				0
			);
		}

		public static string GetGamePadGUID(int index)
		{
			return INTERNAL_guids[index];
		}

		public static void SetGamePadLightBar(int index, Color color)
		{
			IntPtr device = INTERNAL_devices[index];
			if (device == IntPtr.Zero)
			{
				return;
			}

			SDL.SDL_SetGamepadLED(
				device,
				color.R,
				color.G,
				color.B
			);
		}

		public static bool GetGamePadGyro(int index, out Vector3 gyro)
		{
			IntPtr device = INTERNAL_devices[index];
			if (device == IntPtr.Zero)
			{
				gyro = Vector3.Zero;
				return false;
			}

			if (!SDL.SDL_GamepadSensorEnabled(
				device,
				SDL.SDL_SensorType.SDL_SENSOR_GYRO
			)) {
				SDL.SDL_SetGamepadSensorEnabled(
					device,
					SDL.SDL_SensorType.SDL_SENSOR_GYRO,
					true
				);
			}

			unsafe
			{
				float* data = stackalloc float[3];
				if (!SDL.SDL_GetGamepadSensorData(
					device,
					SDL.SDL_SensorType.SDL_SENSOR_GYRO,
					data,
					3
				)) {
					gyro = Vector3.Zero;
					return false;
				}
				gyro.X = data[0];
				gyro.Y = data[1];
				gyro.Z = data[2];
				return true;
			}
		}

		public static bool GetGamePadAccelerometer(int index, out Vector3 accel)
		{
			IntPtr device = INTERNAL_devices[index];
			if (device == IntPtr.Zero)
			{
				accel = Vector3.Zero;
				return false;
			}

			if (!SDL.SDL_GamepadSensorEnabled(
				device,
				SDL.SDL_SensorType.SDL_SENSOR_ACCEL
			)) {
				SDL.SDL_SetGamepadSensorEnabled(
					device,
					SDL.SDL_SensorType.SDL_SENSOR_ACCEL,
					true
				);
			}

			unsafe
			{
				float* data = stackalloc float[3];
				if (!SDL.SDL_GetGamepadSensorData(
					device,
					SDL.SDL_SensorType.SDL_SENSOR_ACCEL,
					data,
					3
				)) {
					accel = Vector3.Zero;
					return false;
				}
				accel.X = data[0];
				accel.Y = data[1];
				accel.Z = data[2];
				return true;
			}
		}

		private static void INTERNAL_AddInstance(uint dev)
		{
			int which = -1;
			for (int i = 0; i < INTERNAL_devices.Length; i += 1)
			{
				if (INTERNAL_devices[i] == IntPtr.Zero)
				{
					which = i;
					break;
				}
			}
			if (which == -1)
			{
				return; // Ignoring more than 4 controllers.
			}

			// Clear the error buffer. We're about to do a LOT of dangerous stuff.
			SDL.SDL_ClearError();

			// Open the device!
			INTERNAL_devices[which] = SDL.SDL_OpenGamepad(dev);

			// We use this when dealing with GUID initialization.
			IntPtr thisJoystick = SDL.SDL_GetGamepadJoystick(INTERNAL_devices[which]);

			// Pair up the instance ID to the player index.
			// FIXME: Remove check after 2.0.4? -flibit
			uint thisInstance = SDL.SDL_GetJoystickID(thisJoystick);
			if (INTERNAL_instanceList.ContainsKey(thisInstance))
			{
				// Duplicate? Usually this is OSX being dumb, but...?
				INTERNAL_devices[which] = IntPtr.Zero;
				return;
			}
			INTERNAL_instanceList.Add(thisInstance, which);

			// Start with a fresh state.
			INTERNAL_states[which] = new GamePadState();
			INTERNAL_states[which].IsConnected = true;

			// Initialize the haptics for the joystick, if applicable.
			bool hasRumble = SDL.SDL_RumbleGamepad(
				INTERNAL_devices[which],
				0,
				0,
				0
			);
			bool hasTriggerRumble = SDL.SDL_RumbleGamepadTriggers(
				INTERNAL_devices[which],
				0,
				0,
				0
			);

			// Need gamepad properties for things like LED
			uint propertiesID = SDL.SDL_GetGamepadProperties(INTERNAL_devices[which]);

			// An SDL_GameController _should_ always be complete...
			GamePadCapabilities caps = new GamePadCapabilities();
			caps.IsConnected = true;
			caps.GamePadType = INTERNAL_gamepadType[(int) SDL.SDL_GetJoystickType(thisJoystick)];
			caps.HasAButton = SDL.SDL_GamepadHasButton(INTERNAL_devices[which], SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH);
			caps.HasBButton = SDL.SDL_GamepadHasButton(INTERNAL_devices[which], SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST);
			caps.HasXButton = SDL.SDL_GamepadHasButton(INTERNAL_devices[which], SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST);
			caps.HasYButton = SDL.SDL_GamepadHasButton(INTERNAL_devices[which], SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH);
			caps.HasBackButton = SDL.SDL_GamepadHasButton(INTERNAL_devices[which], SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_BACK);
			caps.HasBigButton = SDL.SDL_GamepadHasButton(INTERNAL_devices[which], SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_GUIDE);
			caps.HasStartButton = SDL.SDL_GamepadHasButton(INTERNAL_devices[which], SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START);
			caps.HasLeftStickButton = SDL.SDL_GamepadHasButton(INTERNAL_devices[which], SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_STICK);
			caps.HasRightStickButton = SDL.SDL_GamepadHasButton(INTERNAL_devices[which], SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_STICK);
			caps.HasLeftShoulderButton = SDL.SDL_GamepadHasButton(INTERNAL_devices[which], SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER);
			caps.HasRightShoulderButton = SDL.SDL_GamepadHasButton(INTERNAL_devices[which], SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER);
			caps.HasDPadUpButton = SDL.SDL_GamepadHasButton(INTERNAL_devices[which], SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP);
			caps.HasDPadDownButton = SDL.SDL_GamepadHasButton(INTERNAL_devices[which], SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN);
			caps.HasDPadLeftButton = SDL.SDL_GamepadHasButton(INTERNAL_devices[which], SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT);
			caps.HasDPadRightButton = SDL.SDL_GamepadHasButton(INTERNAL_devices[which], SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT);
			caps.HasLeftXThumbStick = SDL.SDL_GamepadHasAxis(INTERNAL_devices[which], SDL.SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX);
			caps.HasLeftYThumbStick = SDL.SDL_GamepadHasAxis(INTERNAL_devices[which], SDL.SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY);
			caps.HasRightXThumbStick = SDL.SDL_GamepadHasAxis(INTERNAL_devices[which], SDL.SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTX);
			caps.HasRightYThumbStick = SDL.SDL_GamepadHasAxis(INTERNAL_devices[which], SDL.SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTY);
			caps.HasLeftTrigger = SDL.SDL_GamepadHasAxis(INTERNAL_devices[which], SDL.SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFT_TRIGGER);
			caps.HasRightTrigger = SDL.SDL_GamepadHasAxis(INTERNAL_devices[which], SDL.SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHT_TRIGGER);
			caps.HasLeftVibrationMotor = hasRumble;
			caps.HasRightVibrationMotor = hasRumble;
			caps.HasVoiceSupport = false;
			caps.HasLightBarEXT = SDL.SDL_GetBooleanProperty(propertiesID, "SDL.joystick.cap.rgb_led", false);
			caps.HasTriggerVibrationMotorsEXT = hasTriggerRumble;
			caps.HasMisc1EXT = SDL.SDL_GamepadHasButton(INTERNAL_devices[which], SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_MISC1);
			caps.HasPaddle1EXT = SDL.SDL_GamepadHasButton(INTERNAL_devices[which], SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_PADDLE1);
			caps.HasPaddle2EXT = SDL.SDL_GamepadHasButton(INTERNAL_devices[which], SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_PADDLE1);
			caps.HasPaddle3EXT = SDL.SDL_GamepadHasButton(INTERNAL_devices[which], SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_PADDLE2);
			caps.HasPaddle4EXT = SDL.SDL_GamepadHasButton(INTERNAL_devices[which], SDL.SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_PADDLE2);
			caps.HasTouchPadEXT = SDL.SDL_GetNumGamepadTouchpads(INTERNAL_devices[which]) > 0;
			caps.HasGyroEXT = SDL.SDL_GamepadHasSensor(INTERNAL_devices[which], SDL.SDL_SensorType.SDL_SENSOR_GYRO);
			caps.HasAccelerometerEXT = SDL.SDL_GamepadHasSensor(INTERNAL_devices[which], SDL.SDL_SensorType.SDL_SENSOR_ACCEL);
			INTERNAL_capabilities[which] = caps;

			/* Store the GUID string for this device
			 * FIXME: Replace GetGUIDEXT string with 3 short values -flibit
			 */
			ushort vendor = SDL.SDL_GetJoystickVendor(thisJoystick);
			ushort product = SDL.SDL_GetJoystickProduct(thisJoystick);
			if (vendor == 0x00 && product == 0x00)
			{
				INTERNAL_guids[which] = "xinput";
			}
			else
			{
				INTERNAL_guids[which] = string.Format(
					"{0:x2}{1:x2}{2:x2}{3:x2}",
					vendor & 0xFF,
					vendor >> 8,
					product & 0xFF,
					product >> 8
				);
			}

			if (vendor == 0x28de) // Valve
			{
				SDL.SDL_GamepadType gct = SDL.SDL_GetGamepadType(INTERNAL_devices[which]);

				if (	gct == SDL.SDL_GamepadType.SDL_GAMEPAD_TYPE_XBOX360 ||
					gct == SDL.SDL_GamepadType.SDL_GAMEPAD_TYPE_XBOXONE	)
				{
					INTERNAL_guids[which] = "xinput";
				}
				else if (gct == SDL.SDL_GamepadType.SDL_GAMEPAD_TYPE_PS4)
				{
					INTERNAL_guids[which] = "4c05c405";
				}
				else if (gct == SDL.SDL_GamepadType.SDL_GAMEPAD_TYPE_PS5)
				{
					INTERNAL_guids[which] = "4c05e60c";
				}
			}

			// Print controller information to stdout.
			string deviceInfo;
			string mapping = SDL.SDL_GetGamepadMapping(INTERNAL_devices[which]);
			if (string.IsNullOrEmpty(mapping))
			{
				deviceInfo = "Mapping not found";
			}
			else
			{
				deviceInfo = "Mapping: " + mapping;
			}
			FNALoggerEXT.LogInfo(
				"Controller " + which.ToString() + ": " +
				SDL.SDL_GetGamepadName(INTERNAL_devices[which]) + ", " +
				"GUID: " + INTERNAL_guids[which] + ", " +
				deviceInfo
			);
		}

		private static void INTERNAL_RemoveInstance(uint dev)
		{
			int output;
			if (!INTERNAL_instanceList.TryGetValue(dev, out output))
			{
				// Odds are, this is controller 5+ getting removed.
				return;
			}
			INTERNAL_instanceList.Remove(dev);
			SDL.SDL_CloseGamepad(INTERNAL_devices[output]);
			INTERNAL_devices[output] = IntPtr.Zero;
			INTERNAL_states[output] = new GamePadState();
			INTERNAL_guids[output] = String.Empty;

			// A lot of errors can happen here, but honestly, they can be ignored...
			SDL.SDL_ClearError();

			FNALoggerEXT.LogInfo("Removed device, player: " + output.ToString());
		}

		private static string[] GenStringArray()
		{
			string[] result = new string[GamePad.GAMEPAD_COUNT];
			for (int i = 0; i < result.Length; i += 1)
			{
				result[i] = String.Empty;
			}
			return result;
		}

		#endregion

		#region Touch Methods

		public static TouchPanelCapabilities GetTouchCapabilities()
		{
			/* Take these reported capabilities with a grain of salt.
			 * On Windows, touch devices won't be detected until they
			 * are interacted with. Also, MaximumTouchCount is completely
			 * bogus. For any touch device, XNA always reports 4.
			 *
			 * -caleb
			 */
			int numDevices;
			SDL.SDL_free(SDL.SDL_GetTouchDevices(out numDevices));
			bool touchDeviceExists = numDevices > 0;
			return new TouchPanelCapabilities(
				touchDeviceExists,
				touchDeviceExists ? 4 : 0
			);
		}

		public static unsafe void UpdateTouchPanelState()
		{
			// Poll the touch device for all active fingers
			int fingers;
			IntPtr fingerArray = SDL.SDL_GetTouchFingers(GetTouchDeviceId(0), out fingers);

			for (int i = 0; i < TouchPanel.MAX_TOUCHES; i += 1)
			{
				if (i >= fingers)
				{
					// No finger found at this index
					TouchPanel.SetFinger(i, TouchPanel.NO_FINGER, Vector2.Zero);
					continue;
				}

				SDL.SDL_Finger* finger = ((SDL.SDL_Finger**) fingerArray)[i];

				// Send the finger data to the TouchPanel
				TouchPanel.SetFinger(
					i,
					(int) finger->id,
					new Vector2(
						(float) Math.Round(finger->x * TouchPanel.DisplayWidth),
						(float) Math.Round(finger->y * TouchPanel.DisplayHeight)
					)
				);
			}

			SDL.SDL_free(fingerArray);
		}

		public static int GetNumTouchFingers()
		{
			int fingers;
			SDL.SDL_free(SDL.SDL_GetTouchFingers(GetTouchDeviceId(0), out fingers));
			return fingers;
		}

		private static unsafe ulong GetTouchDeviceId(int index)
		{
			int touchDeviceCount;
			IntPtr touchDeviceIDs = SDL.SDL_GetTouchDevices(out touchDeviceCount);
			ulong result = index >= 0 && index < touchDeviceCount ? ((ulong*) touchDeviceIDs)[index] : 0;
			SDL.SDL_free(touchDeviceIDs);
			return result;
		}

		#endregion

		#region TextInput Methods

		public static bool IsTextInputActive(IntPtr window)
		{
			return SDL.SDL_TextInputActive(window);
		}

		public static void StartTextInput(IntPtr window)
		{
			SDL.SDL_StartTextInput(window);
		}

		public static void StopTextInput(IntPtr window)
		{
			SDL.SDL_StopTextInput(window);
		}

		#endregion

		#region SDL3<->XNA Key Conversion Methods

		/* From: http://blogs.msdn.com/b/shawnhar/archive/2007/07/02/twin-paths-to-garbage-collector-nirvana.aspx
		 * "If you use an enum type as a dictionary key, internal dictionary operations will cause boxing.
		 * You can avoid this by using integer keys, and casting your enum values to ints before adding
		 * them to the dictionary."
		 */
		private static Dictionary<int, Keys> INTERNAL_keyMap = new Dictionary<int, Keys>()
		{
			{ (int) SDL.SDL_Keycode.SDLK_A,			Keys.A },
			{ (int) SDL.SDL_Keycode.SDLK_B,			Keys.B },
			{ (int) SDL.SDL_Keycode.SDLK_C,			Keys.C },
			{ (int) SDL.SDL_Keycode.SDLK_D,			Keys.D },
			{ (int) SDL.SDL_Keycode.SDLK_E,			Keys.E },
			{ (int) SDL.SDL_Keycode.SDLK_F,			Keys.F },
			{ (int) SDL.SDL_Keycode.SDLK_G,			Keys.G },
			{ (int) SDL.SDL_Keycode.SDLK_H,			Keys.H },
			{ (int) SDL.SDL_Keycode.SDLK_I,			Keys.I },
			{ (int) SDL.SDL_Keycode.SDLK_J,			Keys.J },
			{ (int) SDL.SDL_Keycode.SDLK_K,			Keys.K },
			{ (int) SDL.SDL_Keycode.SDLK_L,			Keys.L },
			{ (int) SDL.SDL_Keycode.SDLK_M,			Keys.M },
			{ (int) SDL.SDL_Keycode.SDLK_N,			Keys.N },
			{ (int) SDL.SDL_Keycode.SDLK_O,			Keys.O },
			{ (int) SDL.SDL_Keycode.SDLK_P,			Keys.P },
			{ (int) SDL.SDL_Keycode.SDLK_Q,			Keys.Q },
			{ (int) SDL.SDL_Keycode.SDLK_R,			Keys.R },
			{ (int) SDL.SDL_Keycode.SDLK_S,			Keys.S },
			{ (int) SDL.SDL_Keycode.SDLK_T,			Keys.T },
			{ (int) SDL.SDL_Keycode.SDLK_U,			Keys.U },
			{ (int) SDL.SDL_Keycode.SDLK_V,			Keys.V },
			{ (int) SDL.SDL_Keycode.SDLK_W,			Keys.W },
			{ (int) SDL.SDL_Keycode.SDLK_X,			Keys.X },
			{ (int) SDL.SDL_Keycode.SDLK_Y,			Keys.Y },
			{ (int) SDL.SDL_Keycode.SDLK_Z,			Keys.Z },
			{ (int) SDL.SDL_Keycode.SDLK_0,			Keys.D0 },
			{ (int) SDL.SDL_Keycode.SDLK_1,			Keys.D1 },
			{ (int) SDL.SDL_Keycode.SDLK_2,			Keys.D2 },
			{ (int) SDL.SDL_Keycode.SDLK_3,			Keys.D3 },
			{ (int) SDL.SDL_Keycode.SDLK_4,			Keys.D4 },
			{ (int) SDL.SDL_Keycode.SDLK_5,			Keys.D5 },
			{ (int) SDL.SDL_Keycode.SDLK_6,			Keys.D6 },
			{ (int) SDL.SDL_Keycode.SDLK_7,			Keys.D7 },
			{ (int) SDL.SDL_Keycode.SDLK_8,			Keys.D8 },
			{ (int) SDL.SDL_Keycode.SDLK_9,			Keys.D9 },
			{ (int) SDL.SDL_Keycode.SDLK_KP_0,		Keys.NumPad0 },
			{ (int) SDL.SDL_Keycode.SDLK_KP_1,		Keys.NumPad1 },
			{ (int) SDL.SDL_Keycode.SDLK_KP_2,		Keys.NumPad2 },
			{ (int) SDL.SDL_Keycode.SDLK_KP_3,		Keys.NumPad3 },
			{ (int) SDL.SDL_Keycode.SDLK_KP_4,		Keys.NumPad4 },
			{ (int) SDL.SDL_Keycode.SDLK_KP_5,		Keys.NumPad5 },
			{ (int) SDL.SDL_Keycode.SDLK_KP_6,		Keys.NumPad6 },
			{ (int) SDL.SDL_Keycode.SDLK_KP_7,		Keys.NumPad7 },
			{ (int) SDL.SDL_Keycode.SDLK_KP_8,		Keys.NumPad8 },
			{ (int) SDL.SDL_Keycode.SDLK_KP_9,		Keys.NumPad9 },
			{ (int) SDL.SDL_Keycode.SDLK_KP_CLEAR,		Keys.OemClear },
			{ (int) SDL.SDL_Keycode.SDLK_KP_DECIMAL,	Keys.Decimal },
			{ (int) SDL.SDL_Keycode.SDLK_KP_DIVIDE,		Keys.Divide },
			{ (int) SDL.SDL_Keycode.SDLK_KP_ENTER,		Keys.Enter },
			{ (int) SDL.SDL_Keycode.SDLK_KP_MINUS,		Keys.Subtract },
			{ (int) SDL.SDL_Keycode.SDLK_KP_MULTIPLY,	Keys.Multiply },
			{ (int) SDL.SDL_Keycode.SDLK_KP_PERIOD,		Keys.OemPeriod },
			{ (int) SDL.SDL_Keycode.SDLK_KP_PLUS,		Keys.Add },
			{ (int) SDL.SDL_Keycode.SDLK_F1,		Keys.F1 },
			{ (int) SDL.SDL_Keycode.SDLK_F2,		Keys.F2 },
			{ (int) SDL.SDL_Keycode.SDLK_F3,		Keys.F3 },
			{ (int) SDL.SDL_Keycode.SDLK_F4,		Keys.F4 },
			{ (int) SDL.SDL_Keycode.SDLK_F5,		Keys.F5 },
			{ (int) SDL.SDL_Keycode.SDLK_F6,		Keys.F6 },
			{ (int) SDL.SDL_Keycode.SDLK_F7,		Keys.F7 },
			{ (int) SDL.SDL_Keycode.SDLK_F8,		Keys.F8 },
			{ (int) SDL.SDL_Keycode.SDLK_F9,		Keys.F9 },
			{ (int) SDL.SDL_Keycode.SDLK_F10,		Keys.F10 },
			{ (int) SDL.SDL_Keycode.SDLK_F11,		Keys.F11 },
			{ (int) SDL.SDL_Keycode.SDLK_F12,		Keys.F12 },
			{ (int) SDL.SDL_Keycode.SDLK_F13,		Keys.F13 },
			{ (int) SDL.SDL_Keycode.SDLK_F14,		Keys.F14 },
			{ (int) SDL.SDL_Keycode.SDLK_F15,		Keys.F15 },
			{ (int) SDL.SDL_Keycode.SDLK_F16,		Keys.F16 },
			{ (int) SDL.SDL_Keycode.SDLK_F17,		Keys.F17 },
			{ (int) SDL.SDL_Keycode.SDLK_F18,		Keys.F18 },
			{ (int) SDL.SDL_Keycode.SDLK_F19,		Keys.F19 },
			{ (int) SDL.SDL_Keycode.SDLK_F20,		Keys.F20 },
			{ (int) SDL.SDL_Keycode.SDLK_F21,		Keys.F21 },
			{ (int) SDL.SDL_Keycode.SDLK_F22,		Keys.F22 },
			{ (int) SDL.SDL_Keycode.SDLK_F23,		Keys.F23 },
			{ (int) SDL.SDL_Keycode.SDLK_F24,		Keys.F24 },
			{ (int) SDL.SDL_Keycode.SDLK_SPACE,		Keys.Space },
			{ (int) SDL.SDL_Keycode.SDLK_UP,		Keys.Up },
			{ (int) SDL.SDL_Keycode.SDLK_DOWN,		Keys.Down },
			{ (int) SDL.SDL_Keycode.SDLK_LEFT,		Keys.Left },
			{ (int) SDL.SDL_Keycode.SDLK_RIGHT,		Keys.Right },
			{ (int) SDL.SDL_Keycode.SDLK_LALT,		Keys.LeftAlt },
			{ (int) SDL.SDL_Keycode.SDLK_RALT,		Keys.RightAlt },
			{ (int) SDL.SDL_Keycode.SDLK_LCTRL,		Keys.LeftControl },
			{ (int) SDL.SDL_Keycode.SDLK_RCTRL,		Keys.RightControl },
			{ (int) SDL.SDL_Keycode.SDLK_LGUI,		Keys.LeftWindows },
			{ (int) SDL.SDL_Keycode.SDLK_RGUI,		Keys.RightWindows },
			{ (int) SDL.SDL_Keycode.SDLK_LSHIFT,		Keys.LeftShift },
			{ (int) SDL.SDL_Keycode.SDLK_RSHIFT,		Keys.RightShift },
			{ (int) SDL.SDL_Keycode.SDLK_APPLICATION,	Keys.Apps },
			{ (int) SDL.SDL_Keycode.SDLK_MENU,		Keys.Apps },
			{ (int) SDL.SDL_Keycode.SDLK_SLASH,		Keys.OemQuestion },
			{ (int) SDL.SDL_Keycode.SDLK_BACKSLASH,		Keys.OemPipe },
			{ (int) SDL.SDL_Keycode.SDLK_LEFTBRACKET,	Keys.OemOpenBrackets },
			{ (int) SDL.SDL_Keycode.SDLK_RIGHTBRACKET,	Keys.OemCloseBrackets },
			{ (int) SDL.SDL_Keycode.SDLK_CAPSLOCK,		Keys.CapsLock },
			{ (int) SDL.SDL_Keycode.SDLK_COMMA,		Keys.OemComma },
			{ (int) SDL.SDL_Keycode.SDLK_DELETE,		Keys.Delete },
			{ (int) SDL.SDL_Keycode.SDLK_END,		Keys.End },
			{ (int) SDL.SDL_Keycode.SDLK_BACKSPACE,		Keys.Back },
			{ (int) SDL.SDL_Keycode.SDLK_RETURN,		Keys.Enter },
			{ (int) SDL.SDL_Keycode.SDLK_ESCAPE,		Keys.Escape },
			{ (int) SDL.SDL_Keycode.SDLK_HOME,		Keys.Home },
			{ (int) SDL.SDL_Keycode.SDLK_INSERT,		Keys.Insert },
			{ (int) SDL.SDL_Keycode.SDLK_MINUS,		Keys.OemMinus },
			{ (int) SDL.SDL_Keycode.SDLK_NUMLOCKCLEAR,	Keys.NumLock },
			{ (int) SDL.SDL_Keycode.SDLK_PAGEUP,		Keys.PageUp },
			{ (int) SDL.SDL_Keycode.SDLK_PAGEDOWN,		Keys.PageDown },
			{ (int) SDL.SDL_Keycode.SDLK_PAUSE,		Keys.Pause },
			{ (int) SDL.SDL_Keycode.SDLK_PERIOD,		Keys.OemPeriod },
			{ (int) SDL.SDL_Keycode.SDLK_EQUALS,		Keys.OemPlus },
			{ (int) SDL.SDL_Keycode.SDLK_PRINTSCREEN,	Keys.PrintScreen },
			{ (int) SDL.SDL_Keycode.SDLK_APOSTROPHE,	Keys.OemQuotes },
			{ (int) SDL.SDL_Keycode.SDLK_SCROLLLOCK,	Keys.Scroll },
			{ (int) SDL.SDL_Keycode.SDLK_SEMICOLON,		Keys.OemSemicolon },
			{ (int) SDL.SDL_Keycode.SDLK_SLEEP,		Keys.Sleep },
			{ (int) SDL.SDL_Keycode.SDLK_TAB,		Keys.Tab },
			{ (int) SDL.SDL_Keycode.SDLK_GRAVE,		Keys.OemTilde },
			{ (int) SDL.SDL_Keycode.SDLK_VOLUMEUP,		Keys.VolumeUp },
			{ (int) SDL.SDL_Keycode.SDLK_VOLUMEDOWN,	Keys.VolumeDown },
			{ '²' /* FIXME: AZERTY SDL3? -flibit */,	Keys.OemTilde },
			{ 'é' /* FIXME: BEPO SDL3? -flibit */,		Keys.None },
			{ '|' /* FIXME: Norwegian SDL3? -flibit */,	Keys.OemPipe },
			{ '+' /* FIXME: Norwegian SDL3? -flibit */,	Keys.OemPlus },
			{ 'ø' /* FIXME: Norwegian SDL3? -flibit */,	Keys.OemSemicolon },
			{ 'æ' /* FIXME: Norwegian SDL3? -flibit */,	Keys.OemQuotes },
			{ (int) SDL.SDL_Keycode.SDLK_UNKNOWN,		Keys.None }
		};
		private static Dictionary<int, Keys> INTERNAL_scanMap = new Dictionary<int, Keys>()
		{
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_A,		Keys.A },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_B,		Keys.B },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_C,		Keys.C },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_D,		Keys.D },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_E,		Keys.E },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_F,		Keys.F },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_G,		Keys.G },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_H,		Keys.H },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_I,		Keys.I },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_J,		Keys.J },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_K,		Keys.K },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_L,		Keys.L },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_M,		Keys.M },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_N,		Keys.N },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_O,		Keys.O },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_P,		Keys.P },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_Q,		Keys.Q },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_R,		Keys.R },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_S,		Keys.S },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_T,		Keys.T },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_U,		Keys.U },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_V,		Keys.V },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_W,		Keys.W },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_X,		Keys.X },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_Y,		Keys.Y },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_Z,		Keys.Z },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_0,		Keys.D0 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_1,		Keys.D1 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_2,		Keys.D2 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_3,		Keys.D3 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_4,		Keys.D4 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_5,		Keys.D5 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_6,		Keys.D6 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_7,		Keys.D7 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_8,		Keys.D8 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_9,		Keys.D9 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_KP_0,		Keys.NumPad0 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_KP_1,		Keys.NumPad1 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_KP_2,		Keys.NumPad2 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_KP_3,		Keys.NumPad3 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_KP_4,		Keys.NumPad4 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_KP_5,		Keys.NumPad5 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_KP_6,		Keys.NumPad6 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_KP_7,		Keys.NumPad7 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_KP_8,		Keys.NumPad8 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_KP_9,		Keys.NumPad9 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_KP_CLEAR,		Keys.OemClear },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_KP_DECIMAL,	Keys.Decimal },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_KP_DIVIDE,	Keys.Divide },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_KP_ENTER,		Keys.Enter },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_KP_MINUS,		Keys.Subtract },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_KP_MULTIPLY,	Keys.Multiply },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_KP_PERIOD,	Keys.OemPeriod },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_KP_PLUS,		Keys.Add },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_F1,		Keys.F1 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_F2,		Keys.F2 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_F3,		Keys.F3 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_F4,		Keys.F4 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_F5,		Keys.F5 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_F6,		Keys.F6 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_F7,		Keys.F7 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_F8,		Keys.F8 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_F9,		Keys.F9 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_F10,		Keys.F10 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_F11,		Keys.F11 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_F12,		Keys.F12 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_F13,		Keys.F13 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_F14,		Keys.F14 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_F15,		Keys.F15 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_F16,		Keys.F16 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_F17,		Keys.F17 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_F18,		Keys.F18 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_F19,		Keys.F19 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_F20,		Keys.F20 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_F21,		Keys.F21 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_F22,		Keys.F22 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_F23,		Keys.F23 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_F24,		Keys.F24 },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_SPACE,		Keys.Space },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_UP,		Keys.Up },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_DOWN,		Keys.Down },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_LEFT,		Keys.Left },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_RIGHT,		Keys.Right },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_LALT,		Keys.LeftAlt },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_RALT,		Keys.RightAlt },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_LCTRL,		Keys.LeftControl },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_RCTRL,		Keys.RightControl },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_LGUI,		Keys.LeftWindows },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_RGUI,		Keys.RightWindows },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_LSHIFT,		Keys.LeftShift },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_RSHIFT,		Keys.RightShift },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_APPLICATION,	Keys.Apps },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_MENU,		Keys.Apps },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_SLASH,		Keys.OemQuestion },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_BACKSLASH,	Keys.OemPipe },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_LEFTBRACKET,	Keys.OemOpenBrackets },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_RIGHTBRACKET,	Keys.OemCloseBrackets },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_CAPSLOCK,		Keys.CapsLock },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_COMMA,		Keys.OemComma },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_DELETE,		Keys.Delete },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_END,		Keys.End },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_BACKSPACE,	Keys.Back },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_RETURN,		Keys.Enter },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_ESCAPE,		Keys.Escape },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_HOME,		Keys.Home },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_INSERT,		Keys.Insert },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_MINUS,		Keys.OemMinus },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_NUMLOCKCLEAR,	Keys.NumLock },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_PAGEUP,		Keys.PageUp },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_PAGEDOWN,		Keys.PageDown },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_PAUSE,		Keys.Pause },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_PERIOD,		Keys.OemPeriod },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_EQUALS,		Keys.OemPlus },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_PRINTSCREEN,	Keys.PrintScreen },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_APOSTROPHE,	Keys.OemQuotes },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_SCROLLLOCK,	Keys.Scroll },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_SEMICOLON,	Keys.OemSemicolon },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_SLEEP,		Keys.Sleep },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_TAB,		Keys.Tab },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_GRAVE,		Keys.OemTilde },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_VOLUMEUP,		Keys.VolumeUp },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_VOLUMEDOWN,	Keys.VolumeDown },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_UNKNOWN,		Keys.None },
			/* FIXME: The following scancodes need verification! */
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_NONUSHASH,	Keys.None },
			{ (int) SDL.SDL_Scancode.SDL_SCANCODE_NONUSBACKSLASH,	Keys.None }
		};
		private static Dictionary<int, SDL.SDL_Scancode> INTERNAL_xnaMap = new Dictionary<int, SDL.SDL_Scancode>()
		{
			{ (int) Keys.A,			SDL.SDL_Scancode.SDL_SCANCODE_A },
			{ (int) Keys.B,			SDL.SDL_Scancode.SDL_SCANCODE_B },
			{ (int) Keys.C,			SDL.SDL_Scancode.SDL_SCANCODE_C },
			{ (int) Keys.D,			SDL.SDL_Scancode.SDL_SCANCODE_D },
			{ (int) Keys.E,			SDL.SDL_Scancode.SDL_SCANCODE_E },
			{ (int) Keys.F,			SDL.SDL_Scancode.SDL_SCANCODE_F },
			{ (int) Keys.G,			SDL.SDL_Scancode.SDL_SCANCODE_G },
			{ (int) Keys.H,			SDL.SDL_Scancode.SDL_SCANCODE_H },
			{ (int) Keys.I,			SDL.SDL_Scancode.SDL_SCANCODE_I },
			{ (int) Keys.J,			SDL.SDL_Scancode.SDL_SCANCODE_J },
			{ (int) Keys.K,			SDL.SDL_Scancode.SDL_SCANCODE_K },
			{ (int) Keys.L,			SDL.SDL_Scancode.SDL_SCANCODE_L },
			{ (int) Keys.M,			SDL.SDL_Scancode.SDL_SCANCODE_M },
			{ (int) Keys.N,			SDL.SDL_Scancode.SDL_SCANCODE_N },
			{ (int) Keys.O,			SDL.SDL_Scancode.SDL_SCANCODE_O },
			{ (int) Keys.P,			SDL.SDL_Scancode.SDL_SCANCODE_P },
			{ (int) Keys.Q,			SDL.SDL_Scancode.SDL_SCANCODE_Q },
			{ (int) Keys.R,			SDL.SDL_Scancode.SDL_SCANCODE_R },
			{ (int) Keys.S,			SDL.SDL_Scancode.SDL_SCANCODE_S },
			{ (int) Keys.T,			SDL.SDL_Scancode.SDL_SCANCODE_T },
			{ (int) Keys.U,			SDL.SDL_Scancode.SDL_SCANCODE_U },
			{ (int) Keys.V,			SDL.SDL_Scancode.SDL_SCANCODE_V },
			{ (int) Keys.W,			SDL.SDL_Scancode.SDL_SCANCODE_W },
			{ (int) Keys.X,			SDL.SDL_Scancode.SDL_SCANCODE_X },
			{ (int) Keys.Y,			SDL.SDL_Scancode.SDL_SCANCODE_Y },
			{ (int) Keys.Z,			SDL.SDL_Scancode.SDL_SCANCODE_Z },
			{ (int) Keys.D0,		SDL.SDL_Scancode.SDL_SCANCODE_0 },
			{ (int) Keys.D1,		SDL.SDL_Scancode.SDL_SCANCODE_1 },
			{ (int) Keys.D2,		SDL.SDL_Scancode.SDL_SCANCODE_2 },
			{ (int) Keys.D3,		SDL.SDL_Scancode.SDL_SCANCODE_3 },
			{ (int) Keys.D4,		SDL.SDL_Scancode.SDL_SCANCODE_4 },
			{ (int) Keys.D5,		SDL.SDL_Scancode.SDL_SCANCODE_5 },
			{ (int) Keys.D6,		SDL.SDL_Scancode.SDL_SCANCODE_6 },
			{ (int) Keys.D7,		SDL.SDL_Scancode.SDL_SCANCODE_7 },
			{ (int) Keys.D8,		SDL.SDL_Scancode.SDL_SCANCODE_8 },
			{ (int) Keys.D9,		SDL.SDL_Scancode.SDL_SCANCODE_9 },
			{ (int) Keys.NumPad0,		SDL.SDL_Scancode.SDL_SCANCODE_KP_0 },
			{ (int) Keys.NumPad1,		SDL.SDL_Scancode.SDL_SCANCODE_KP_1 },
			{ (int) Keys.NumPad2,		SDL.SDL_Scancode.SDL_SCANCODE_KP_2 },
			{ (int) Keys.NumPad3,		SDL.SDL_Scancode.SDL_SCANCODE_KP_3 },
			{ (int) Keys.NumPad4,		SDL.SDL_Scancode.SDL_SCANCODE_KP_4 },
			{ (int) Keys.NumPad5,		SDL.SDL_Scancode.SDL_SCANCODE_KP_5 },
			{ (int) Keys.NumPad6,		SDL.SDL_Scancode.SDL_SCANCODE_KP_6 },
			{ (int) Keys.NumPad7,		SDL.SDL_Scancode.SDL_SCANCODE_KP_7 },
			{ (int) Keys.NumPad8,		SDL.SDL_Scancode.SDL_SCANCODE_KP_8 },
			{ (int) Keys.NumPad9,		SDL.SDL_Scancode.SDL_SCANCODE_KP_9 },
			{ (int) Keys.OemClear,		SDL.SDL_Scancode.SDL_SCANCODE_KP_CLEAR },
			{ (int) Keys.Decimal,		SDL.SDL_Scancode.SDL_SCANCODE_KP_DECIMAL },
			{ (int) Keys.Divide,		SDL.SDL_Scancode.SDL_SCANCODE_KP_DIVIDE },
			{ (int) Keys.Multiply,		SDL.SDL_Scancode.SDL_SCANCODE_KP_MULTIPLY },
			{ (int) Keys.Subtract,		SDL.SDL_Scancode.SDL_SCANCODE_KP_MINUS },
			{ (int) Keys.Add,		SDL.SDL_Scancode.SDL_SCANCODE_KP_PLUS },
			{ (int) Keys.F1,		SDL.SDL_Scancode.SDL_SCANCODE_F1 },
			{ (int) Keys.F2,		SDL.SDL_Scancode.SDL_SCANCODE_F2 },
			{ (int) Keys.F3,		SDL.SDL_Scancode.SDL_SCANCODE_F3 },
			{ (int) Keys.F4,		SDL.SDL_Scancode.SDL_SCANCODE_F4 },
			{ (int) Keys.F5,		SDL.SDL_Scancode.SDL_SCANCODE_F5 },
			{ (int) Keys.F6,		SDL.SDL_Scancode.SDL_SCANCODE_F6 },
			{ (int) Keys.F7,		SDL.SDL_Scancode.SDL_SCANCODE_F7 },
			{ (int) Keys.F8,		SDL.SDL_Scancode.SDL_SCANCODE_F8 },
			{ (int) Keys.F9,		SDL.SDL_Scancode.SDL_SCANCODE_F9 },
			{ (int) Keys.F10,		SDL.SDL_Scancode.SDL_SCANCODE_F10 },
			{ (int) Keys.F11,		SDL.SDL_Scancode.SDL_SCANCODE_F11 },
			{ (int) Keys.F12,		SDL.SDL_Scancode.SDL_SCANCODE_F12 },
			{ (int) Keys.F13,		SDL.SDL_Scancode.SDL_SCANCODE_F13 },
			{ (int) Keys.F14,		SDL.SDL_Scancode.SDL_SCANCODE_F14 },
			{ (int) Keys.F15,		SDL.SDL_Scancode.SDL_SCANCODE_F15 },
			{ (int) Keys.F16,		SDL.SDL_Scancode.SDL_SCANCODE_F16 },
			{ (int) Keys.F17,		SDL.SDL_Scancode.SDL_SCANCODE_F17 },
			{ (int) Keys.F18,		SDL.SDL_Scancode.SDL_SCANCODE_F18 },
			{ (int) Keys.F19,		SDL.SDL_Scancode.SDL_SCANCODE_F19 },
			{ (int) Keys.F20,		SDL.SDL_Scancode.SDL_SCANCODE_F20 },
			{ (int) Keys.F21,		SDL.SDL_Scancode.SDL_SCANCODE_F21 },
			{ (int) Keys.F22,		SDL.SDL_Scancode.SDL_SCANCODE_F22 },
			{ (int) Keys.F23,		SDL.SDL_Scancode.SDL_SCANCODE_F23 },
			{ (int) Keys.F24,		SDL.SDL_Scancode.SDL_SCANCODE_F24 },
			{ (int) Keys.Space,		SDL.SDL_Scancode.SDL_SCANCODE_SPACE },
			{ (int) Keys.Up,		SDL.SDL_Scancode.SDL_SCANCODE_UP },
			{ (int) Keys.Down,		SDL.SDL_Scancode.SDL_SCANCODE_DOWN },
			{ (int) Keys.Left,		SDL.SDL_Scancode.SDL_SCANCODE_LEFT },
			{ (int) Keys.Right,		SDL.SDL_Scancode.SDL_SCANCODE_RIGHT },
			{ (int) Keys.LeftAlt,		SDL.SDL_Scancode.SDL_SCANCODE_LALT },
			{ (int) Keys.RightAlt,		SDL.SDL_Scancode.SDL_SCANCODE_RALT },
			{ (int) Keys.LeftControl,	SDL.SDL_Scancode.SDL_SCANCODE_LCTRL },
			{ (int) Keys.RightControl,	SDL.SDL_Scancode.SDL_SCANCODE_RCTRL },
			{ (int) Keys.LeftWindows,	SDL.SDL_Scancode.SDL_SCANCODE_LGUI },
			{ (int) Keys.RightWindows,	SDL.SDL_Scancode.SDL_SCANCODE_RGUI },
			{ (int) Keys.LeftShift,		SDL.SDL_Scancode.SDL_SCANCODE_LSHIFT },
			{ (int) Keys.RightShift,	SDL.SDL_Scancode.SDL_SCANCODE_RSHIFT },
			{ (int) Keys.Apps,		SDL.SDL_Scancode.SDL_SCANCODE_APPLICATION },
			{ (int) Keys.OemQuestion,	SDL.SDL_Scancode.SDL_SCANCODE_SLASH },
			{ (int) Keys.OemPipe,		SDL.SDL_Scancode.SDL_SCANCODE_BACKSLASH },
			{ (int) Keys.OemOpenBrackets,	SDL.SDL_Scancode.SDL_SCANCODE_LEFTBRACKET },
			{ (int) Keys.OemCloseBrackets,	SDL.SDL_Scancode.SDL_SCANCODE_RIGHTBRACKET },
			{ (int) Keys.CapsLock,		SDL.SDL_Scancode.SDL_SCANCODE_CAPSLOCK },
			{ (int) Keys.OemComma,		SDL.SDL_Scancode.SDL_SCANCODE_COMMA },
			{ (int) Keys.Delete,		SDL.SDL_Scancode.SDL_SCANCODE_DELETE },
			{ (int) Keys.End,		SDL.SDL_Scancode.SDL_SCANCODE_END },
			{ (int) Keys.Back,		SDL.SDL_Scancode.SDL_SCANCODE_BACKSPACE },
			{ (int) Keys.Enter,		SDL.SDL_Scancode.SDL_SCANCODE_RETURN },
			{ (int) Keys.Escape,		SDL.SDL_Scancode.SDL_SCANCODE_ESCAPE },
			{ (int) Keys.Home,		SDL.SDL_Scancode.SDL_SCANCODE_HOME },
			{ (int) Keys.Insert,		SDL.SDL_Scancode.SDL_SCANCODE_INSERT },
			{ (int) Keys.OemMinus,		SDL.SDL_Scancode.SDL_SCANCODE_MINUS },
			{ (int) Keys.NumLock,		SDL.SDL_Scancode.SDL_SCANCODE_NUMLOCKCLEAR },
			{ (int) Keys.PageUp,		SDL.SDL_Scancode.SDL_SCANCODE_PAGEUP },
			{ (int) Keys.PageDown,		SDL.SDL_Scancode.SDL_SCANCODE_PAGEDOWN },
			{ (int) Keys.Pause,		SDL.SDL_Scancode.SDL_SCANCODE_PAUSE },
			{ (int) Keys.OemPeriod,		SDL.SDL_Scancode.SDL_SCANCODE_PERIOD },
			{ (int) Keys.OemPlus,		SDL.SDL_Scancode.SDL_SCANCODE_EQUALS },
			{ (int) Keys.PrintScreen,	SDL.SDL_Scancode.SDL_SCANCODE_PRINTSCREEN },
			{ (int) Keys.OemQuotes,		SDL.SDL_Scancode.SDL_SCANCODE_APOSTROPHE },
			{ (int) Keys.Scroll,		SDL.SDL_Scancode.SDL_SCANCODE_SCROLLLOCK },
			{ (int) Keys.OemSemicolon,	SDL.SDL_Scancode.SDL_SCANCODE_SEMICOLON },
			{ (int) Keys.Sleep,		SDL.SDL_Scancode.SDL_SCANCODE_SLEEP },
			{ (int) Keys.Tab,		SDL.SDL_Scancode.SDL_SCANCODE_TAB },
			{ (int) Keys.OemTilde,		SDL.SDL_Scancode.SDL_SCANCODE_GRAVE },
			{ (int) Keys.VolumeUp,		SDL.SDL_Scancode.SDL_SCANCODE_VOLUMEUP },
			{ (int) Keys.VolumeDown,	SDL.SDL_Scancode.SDL_SCANCODE_VOLUMEDOWN },
			{ (int) Keys.None,		SDL.SDL_Scancode.SDL_SCANCODE_UNKNOWN }
		};

		private static Keys ToXNAKey(ref uint sym, ref SDL.SDL_Scancode scancode)
		{
			Keys retVal;
			if (UseScancodes)
			{
				if (INTERNAL_scanMap.TryGetValue((int) scancode, out retVal))
				{
					return retVal;
				}
			}
			else
			{
				if (INTERNAL_keyMap.TryGetValue((int) sym, out retVal))
				{
					return retVal;
				}
			}
			FNALoggerEXT.LogWarn(
				"KEY/SCANCODE MISSING FROM SDL3->XNA DICTIONARY: " +
				sym.ToString() + " " +
				scancode.ToString()
			);
			return Keys.None;
		}

		public static Keys GetKeyFromScancode(Keys scancode)
		{
			if (UseScancodes)
			{
				return scancode;
			}
			SDL.SDL_Scancode retVal;
			if (INTERNAL_xnaMap.TryGetValue((int) scancode, out retVal))
			{
				Keys result;
				// FIXME SDL3: Do we need mod state?
				uint sym = SDL.SDL_GetKeyFromScancode(retVal, 0, true);
				if (INTERNAL_keyMap.TryGetValue((int) sym, out result))
				{
					return result;
				}
				FNALoggerEXT.LogWarn(
					"KEYCODE MISSING FROM SDL3->XNA DICTIONARY: " +
					sym.ToString()
				);
			}
			else
			{
				FNALoggerEXT.LogWarn(
					"SCANCODE MISSING FROM XNA->SDL3 DICTIONARY: " +
					scancode.ToString()
				);
			}
			return Keys.None;
		}

		#endregion

		#region Private Static Win32 WM_PAINT Interop

		private static SDL.SDL_EventFilter win32OnPaint = Win32OnPaint;
		private static SDL.SDL_EventFilter prevEventFilter;
		private static unsafe bool Win32OnPaint(IntPtr userdata, SDL.SDL_Event* evt)
		{
			if (evt->type == (uint) SDL.SDL_EventType.SDL_EVENT_WINDOW_EXPOSED)
			{
				foreach (Game game in activeGames)
				{
					if (	game.Window != null &&
						evt->window.windowID == SDL.SDL_GetWindowID(game.Window.Handle)	)
					{
						game.RedrawWindow();
						return false;
					}
				}
			}
			if (prevEventFilter != null)
			{
				return prevEventFilter(userdata, evt);
			}
			return true;
		}

		#endregion
	}
}
