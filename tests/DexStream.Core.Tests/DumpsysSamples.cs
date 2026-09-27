namespace DexStream.Core.Tests;

/// <summary>
/// Representative <c>dumpsys display</c> output. Field names, ordering and nesting follow the real
/// dump from a Samsung One UI device on Android 14; values are trimmed to what the parser reads.
/// </summary>
internal static class DumpsysSamples
{
    /// <summary>A phone with its own panel plus an active Samsung DeX desktop.</summary>
    public const string PhoneWithDex = """
        DISPLAY MANAGER (dumpsys display)
          mOnlyCore=false
          mSafeMode=false
          mPendingTraversal=false
          mGlobalDisplayState=ON

        Display Adapters: size=3
          LocalDisplayAdapter
          VirtualDisplayAdapter
          OverlayDisplayAdapter

        Display Devices: size=2
          DisplayDeviceInfo{"Built-in Screen": uniqueId="local:4619827259835644672", 1440 x 3120, modeId 2, renderFrameRate 120.0, defaultModeId 1, supportedModes [{id=1, width=1440, height=3120, fps=60.000004, alternativeRefreshRates=[120.0]}, {id=2, width=1440, height=3120, fps=120.00001, alternativeRefreshRates=[60.000004]}, {id=3, width=1080, height=2340, fps=120.00001, alternativeRefreshRates=[60.000004]}], colorMode 0, supportedColorModes [0, 7, 9], hdrCapabilities HdrCapabilities{mSupportedHdrTypes=[2, 3, 4], mMaxLuminance=1200.0, mMaxAverageLuminance=420.0, mMinLuminance=0.0}, allmSupported false, gameContentTypeSupported false, density 600, 600.0 x 600.0 dpi, appVsyncOff 1000000, presDeadline 11500000, touch INTERNAL, rotation 0, type INTERNAL, address {port=0, model=0x4c0f2bdd4a8f}, deviceProductInfo DeviceProductInfo{name=, manufacturerPnpId=SEC, productId=0, modelYear=null, manufactureDate=ManufactureDate{week=1, year=2024}, connectionToSinkType=1}, state ON, committedState ON, frameRateOverride , brightnessMinimum 0.0, brightnessMaximum 1.0, brightnessDefault 0.39763778, FLAG_DEFAULT_DISPLAY, FLAG_ROTATES_WITH_CONTENT, FLAG_SECURE, FLAG_SUPPORTS_PROTECTED_BUFFERS, FLAG_TRUSTED}
          DisplayDeviceInfo{"Samsung DeX": uniqueId="virtual:com.samsung.android.desktopmode,10001,DeX", 3840 x 2160, modeId 7, renderFrameRate 60.0, defaultModeId 7, supportedModes [{id=7, width=3840, height=2160, fps=60.0, alternativeRefreshRates=[]}, {id=8, width=2560, height=1440, fps=60.0, alternativeRefreshRates=[]}], colorMode 0, supportedColorModes [0], hdrCapabilities null, allmSupported false, gameContentTypeSupported false, density 240, 240.0 x 240.0 dpi, appVsyncOff 0, presDeadline 16666666, touch NONE, rotation 0, type VIRTUAL, deviceProductInfo null, state ON, committedState ON, frameRateOverride , brightnessMinimum 0.0, brightnessMaximum 0.0, brightnessDefault 0.0, FLAG_PRESENTATION, FLAG_OWN_CONTENT_ONLY, FLAG_TRUSTED}

        Logical Displays: size=2
          Display 0:
            mDisplayId=0
            mPhase=1
            mLayerStack=0
            mHasContent=true
            mDesiredDisplayModeSpecs={baseModeId=2 allowGroupSwitching=false primaryRefreshRateRange=[60 120] appRequestRefreshRateRange=[0 Infinity]}
            mRequestedColorMode=0
            mDisplayOffset=(0, 0)
            mDisplayScalingDisabled=false
            mPrimaryDisplayDevice=Built-in Screen
            mBaseDisplayInfo=DisplayInfo{"Built-in Screen", displayId 0, displayGroupId 0, FLAG_SECURE, real 1440 x 3120}
          Display 2:
            mDisplayId=2
            mPhase=1
            mLayerStack=2
            mHasContent=true
            mDesiredDisplayModeSpecs={baseModeId=7 allowGroupSwitching=false primaryRefreshRateRange=[0 Infinity] appRequestRefreshRateRange=[0 Infinity]}
            mRequestedColorMode=0
            mDisplayOffset=(0, 0)
            mDisplayScalingDisabled=false
            mPrimaryDisplayDevice=Samsung DeX
            mBaseDisplayInfo=DisplayInfo{"Samsung DeX", displayId 2, displayGroupId 0, FLAG_PRESENTATION, real 3840 x 2160}
        """;

    /// <summary>A phone with no DeX session running.</summary>
    public const string PhoneOnly = """
        Display Devices: size=1
          DisplayDeviceInfo{"Built-in Screen": uniqueId="local:4619827259835644672", 1080 x 2340, modeId 1, defaultModeId 1, supportedModes [{id=1, width=1080, height=2340, fps=60.000004, alternativeRefreshRates=[]}], colorMode 0, supportedColorModes [0], density 420, 420.0 x 420.0 dpi, touch INTERNAL, rotation 0, type INTERNAL, state ON, committedState ON, FLAG_DEFAULT_DISPLAY, FLAG_ROTATES_WITH_CONTENT, FLAG_SECURE, FLAG_TRUSTED}

        Logical Displays: size=1
          Display 0:
            mDisplayId=0
            mPrimaryDisplayDevice=Built-in Screen
        """;

    /// <summary>A display created by the DexStream agent, plus the phone panel switched off.</summary>
    public const string AgentCreatedDisplay = """
        Display Devices: size=2
          DisplayDeviceInfo{"Built-in Screen": uniqueId="local:1", 1440 x 3120, modeId 1, defaultModeId 1, supportedModes [{id=1, width=1440, height=3120, fps=60.0, alternativeRefreshRates=[]}], density 600, touch INTERNAL, type INTERNAL, state OFF, committedState OFF, FLAG_DEFAULT_DISPLAY, FLAG_SECURE}
          DisplayDeviceInfo{"DexStream Desktop": uniqueId="virtual:dexstream-agent", 2560 x 1440, modeId 4, defaultModeId 4, supportedModes [{id=4, width=2560, height=1440, fps=120.0, alternativeRefreshRates=[]}], density 240, touch NONE, type VIRTUAL, state ON, committedState ON, FLAG_PRESENTATION, FLAG_OWN_CONTENT_ONLY, FLAG_TRUSTED}

        Logical Displays: size=2
          Display 0:
            mDisplayId=0
            mPrimaryDisplayDevice=Built-in Screen
          Display 5:
            mDisplayId=5
            mPrimaryDisplayDevice=DexStream Desktop
        """;
}
