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
using System.Runtime.InteropServices;
#endregion

namespace Microsoft.Xna.Framework.Graphics
{
	/// <summary>
	/// Windows-only DXGI helper for querying the D3D adapter FNA3D is rendering on.
	/// <see cref="GraphicsAdapter.Description"/> returns the SDL display (monitor) name on
	/// FNA, which is not what callers asking "which GPU is the process running on?" want.
	/// This helper P/Invokes DXGI and mirrors FNA3D's D3D11 adapter selection
	/// (see FNA3D_Driver_D3D11.c:5544-5570): it tries IDXGIFactory6::EnumAdapterByGpuPreference
	/// with HIGH_PERFORMANCE by default, MINIMUM_POWER when the FNA3D_PREFER_LOW_POWER
	/// env var is set; falls back to IDXGIFactory1::EnumAdapters1(0) on older Windows
	/// where IDXGIFactory6 is unavailable, matching FNA3D's own fallback.
	/// Returns null on non-Windows platforms or if DXGI is unavailable.
	/// </summary>
	internal static class WindowsAdapterInfo
	{
		// IID_IDXGIFactory1: 770aae78-f26f-4dba-a829-253c83d1b387
		private static Guid IID_IDXGIFactory1 = new Guid(
			0x770aae78, 0xf26f, 0x4dba,
			0xa8, 0x29, 0x25, 0x3c, 0x83, 0xd1, 0xb3, 0x87
		);

		// IID_IDXGIFactory6: c1b6694f-ff09-44a9-b03c-77900a0a1d17
		// (matches D3D_IID_IDXGIFactory6 in lib/FNA3D/src/FNA3D_Driver_D3D11.h:43)
		private static Guid IID_IDXGIFactory6 = new Guid(
			0xc1b6694f, 0xff09, 0x44a9,
			0xb0, 0x3c, 0x77, 0x90, 0x0a, 0x0a, 0x1d, 0x17
		);

		// IID_IDXGIAdapter1: 29038f61-3839-4626-91fd-086879011a05
		// (matches D3D_IID_IDXGIAdapter1 in lib/FNA3D/src/FNA3D_Driver_D3D11.h:45)
		private static Guid IID_IDXGIAdapter1 = new Guid(
			0x29038f61, 0x3839, 0x4626,
			0x91, 0xfd, 0x08, 0x68, 0x79, 0x01, 0x1a, 0x05
		);

		// DXGI_GPU_PREFERENCE values from dxgi1_6.h (also in FNA3D_Driver_D3D11.h:724-727).
		private const int DXGI_GPU_PREFERENCE_MINIMUM_POWER = 1;
		private const int DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE = 2;

		// SDL hint name that FNA3D's D3D11 backend reads at FNA3D_Driver_D3D11.c:5555.
		// SDL_GetHintBoolean falls back to the process env var of the same name, so a
		// managed-side Environment.GetEnvironmentVariable check sees the same value
		// FNA3D's call would have seen.
		private const string Fna3dLowPowerEnvVar = "FNA3D_PREFER_LOW_POWER";

		[DllImport("dxgi.dll", CallingConvention = CallingConvention.StdCall)]
		private static extern unsafe int CreateDXGIFactory1(Guid* riid, IntPtr* ppFactory);

		// Mirrors DXGI_ADAPTER_DESC1 layout. Blittable: char[128] = WCHAR[128] = 256 bytes,
		// then four uint32, three nuint, one LUID (8 bytes), one uint32.
		[StructLayout(LayoutKind.Sequential)]
		private unsafe struct DXGI_ADAPTER_DESC1
		{
			public fixed char Description[128];
			public uint VendorId;
			public uint DeviceId;
			public uint SubSysId;
			public uint Revision;
			public IntPtr DedicatedVideoMemory;
			public IntPtr DedicatedSystemMemory;
			public IntPtr SharedSystemMemory;
			public long AdapterLuid;
			public uint Flags;
		}

		/// <summary>
		/// Returns the description string of the DXGI adapter FNA3D's D3D11 backend
		/// renders with, or null if not on Windows, dxgi.dll isn't loadable, or the
		/// query fails for any reason. Mirrors FNA3D_Driver_D3D11.c:5544-5570:
		/// IDXGIFactory6::EnumAdapterByGpuPreference with HIGH_PERFORMANCE by default,
		/// MINIMUM_POWER when FNA3D_PREFER_LOW_POWER is set; falls back to
		/// IDXGIFactory1::EnumAdapters1(0) on older Windows. Best-effort: never throws.
		/// </summary>
		public static string GetPrimaryAdapterDescription()
		{
			if (!OperatingSystem.IsWindows())
			{
				return null;
			}

			IntPtr factory = IntPtr.Zero;
			IntPtr factory6 = IntPtr.Zero;
			IntPtr adapter = IntPtr.Zero;
			try
			{
				unsafe
				{
					Guid factoryGuid = IID_IDXGIFactory1;
					IntPtr factoryOut;
					int hr = CreateDXGIFactory1(&factoryGuid, &factoryOut);
					if (hr < 0 || factoryOut == IntPtr.Zero)
					{
						return null;
					}
					factory = factoryOut;

					// Try IDXGIFactory6 path first: it lets us ask the OS for the
					// HIGH_PERFORMANCE (or MINIMUM_POWER) adapter, matching what
					// FNA3D's D3D11 device creation does. EnumAdapters1(0) alone
					// returns the Windows per-app default, which on hybrid systems
					// follows the per-app preference registry, a path FNA3D bypasses.
					Guid factory6Guid = IID_IDXGIFactory6;
					IntPtr factory6Out;
					// IUnknown vtable slot 0: QueryInterface.
					void** factoryVtbl = *(void***) factory;
					var queryInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>) factoryVtbl[0];
					hr = queryInterface(factory, &factory6Guid, &factory6Out);
					if (hr >= 0 && factory6Out != IntPtr.Zero)
					{
						factory6 = factory6Out;

						int gpuPref =
							string.Equals(
								Environment.GetEnvironmentVariable(Fna3dLowPowerEnvVar),
								"1",
								StringComparison.Ordinal
							) ?
								DXGI_GPU_PREFERENCE_MINIMUM_POWER :
								DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE;

						// IDXGIFactory6 vtable slot 29: EnumAdapterByGpuPreference.
						// (Slots 0-12 from IDXGIFactory1, 13 IsCurrent, 14-24 IDXGIFactory2,
						// 25 GetCreationFlags (Factory3), 26-27 Factory4, 28 CheckFeatureSupport
						// (Factory5), 29 EnumAdapterByGpuPreference. Layout confirmed against
						// FNA3D_Driver_D3D11.h:730-886.)
						Guid adapterGuid = IID_IDXGIAdapter1;
						IntPtr adapterOut;
						void** factory6Vtbl = *(void***) factory6;
						var enumByPref = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, Guid*, IntPtr*, int>) factory6Vtbl[29];
						hr = enumByPref(factory6, 0, gpuPref, &adapterGuid, &adapterOut);
						if (hr >= 0 && adapterOut != IntPtr.Zero)
						{
							adapter = adapterOut;
						}
					}

					if (adapter == IntPtr.Zero)
					{
						// Fallback path matches FNA3D_Driver_D3D11.c:5563-5570:
						// IDXGIFactory6 unavailable (Win10 pre-1803) or the call failed:
						// take whatever EnumAdapters1(0) returns.
						// IDXGIFactory1 vtable slot 12: EnumAdapters1.
						//   0-2: IUnknown (QueryInterface, AddRef, Release)
						//   3-6: IDXGIObject (SetPrivateData, SetPrivateDataInterface, GetPrivateData, GetParent)
						//   7-11: IDXGIFactory (EnumAdapters, MakeWindowAssociation, GetWindowAssociation, CreateSwapChain, CreateSoftwareAdapter)
						//   12: IDXGIFactory1::EnumAdapters1
						var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>) factoryVtbl[12];
						IntPtr adapterOut;
						hr = enumAdapters1(factory, 0, &adapterOut);
						if (hr < 0 || adapterOut == IntPtr.Zero)
						{
							return null;
						}
						adapter = adapterOut;
					}

					// IDXGIAdapter1 vtable slot 10: GetDesc1.
					//   0-2: IUnknown
					//   3-6: IDXGIObject
					//   7-9: IDXGIAdapter (EnumOutputs, GetDesc, CheckInterfaceSupport)
					//   10: IDXGIAdapter1::GetDesc1
					void** adapterVtbl = *(void***) adapter;
					var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, DXGI_ADAPTER_DESC1*, int>) adapterVtbl[10];
					DXGI_ADAPTER_DESC1 desc = default;
					hr = getDesc1(adapter, &desc);
					if (hr < 0)
					{
						return null;
					}

					return new string(desc.Description).TrimEnd('\0');
				}
			}
			catch
			{
				// dxgi.dll missing, COM call failed unexpectedly; adapter info is best-effort
				return null;
			}
			finally
			{
				if (adapter != IntPtr.Zero)
				{
					ReleaseCom(adapter);
				}
				if (factory6 != IntPtr.Zero)
				{
					ReleaseCom(factory6);
				}
				if (factory != IntPtr.Zero)
				{
					ReleaseCom(factory);
				}
			}
		}

		private static unsafe void ReleaseCom(IntPtr obj)
		{
			try
			{
				void** vtbl = *(void***) obj;
				var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>) vtbl[2];
				release(obj);
			}
			catch
			{
				// best-effort during cleanup
			}
		}
	}
}
