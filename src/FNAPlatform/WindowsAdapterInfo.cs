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
	/// Windows-only DXGI helper for querying the primary D3D adapter's description.
	/// <see cref="GraphicsAdapter.Description"/> returns the SDL display (monitor) name on
	/// FNA, which is not what callers asking "which GPU is the process running on?" want.
	/// This helper P/Invokes DXGI to return the actual GPU vendor + model string
	/// (e.g. "NVIDIA GeForce RTX 5080"), which reflects per-process GPU selection
	/// including Windows per-app GPU preferences.
	/// Returns null on non-Windows platforms or if DXGI is unavailable.
	/// </summary>
	internal static class WindowsAdapterInfo
	{
		// IID_IDXGIFactory1: 770aae78-f26f-4dba-a829-253c83d1b387
		private static Guid IID_IDXGIFactory1 = new Guid(
			0x770aae78, 0xf26f, 0x4dba,
			0xa8, 0x29, 0x25, 0x3c, 0x83, 0xd1, 0xb3, 0x87
		);

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
		/// Returns the description string of the primary DXGI adapter (the one D3D would
		/// default to for the calling process, honoring Windows per-app GPU preferences),
		/// or null if not on Windows, dxgi.dll isn't loadable, or the query fails for any
		/// reason. Best-effort: never throws.
		/// </summary>
		public static string GetPrimaryAdapterDescription()
		{
			if (!OperatingSystem.IsWindows())
			{
				return null;
			}

			IntPtr factory = IntPtr.Zero;
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

					// IDXGIFactory1 vtable slot 12: EnumAdapters1.
					//   0-2: IUnknown (QueryInterface, AddRef, Release)
					//   3-6: IDXGIObject (SetPrivateData, SetPrivateDataInterface, GetPrivateData, GetParent)
					//   7-11: IDXGIFactory (EnumAdapters, MakeWindowAssociation, GetWindowAssociation, CreateSwapChain, CreateSoftwareAdapter)
					//   12: IDXGIFactory1::EnumAdapters1
					void** factoryVtbl = *(void***) factory;
					var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>) factoryVtbl[12];
					IntPtr adapterOut;
					hr = enumAdapters1(factory, 0, &adapterOut);
					if (hr < 0 || adapterOut == IntPtr.Zero)
					{
						return null;
					}
					adapter = adapterOut;

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
				// dxgi.dll missing, COM call failed unexpectedly — adapter info is best-effort
				return null;
			}
			finally
			{
				if (adapter != IntPtr.Zero)
				{
					ReleaseCom(adapter);
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
