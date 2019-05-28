﻿using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.Assertions;

namespace UnityEngine.Rendering.PostProcessing
{
#if UNITY_2017_2_OR_NEWER
    using XRSettings = UnityEngine.XR.XRSettings;
#elif UNITY_5_6_OR_NEWER
    using XRSettings = UnityEngine.VR.VRSettings;
#endif

    // TODO: XMLDoc everything (?)
    [DisallowMultipleComponent, ExecuteInEditMode, ImageEffectAllowedInSceneView]
    [AddComponentMenu("Rendering/Readout-Post-process Layer", 1000)]
    [RequireComponent(typeof(Camera))]
    public class ReadoutPostProcessLayer : MonoBehaviour
    {

        // Settings
        public Transform volumeTrigger;
        public LayerMask volumeLayer;
        public bool stopNaNPropagation = true;

        // Builtins / hardcoded effects that don't benefit from volume blending
        public PostProcessLayer.Antialiasing antialiasingMode = PostProcessLayer.Antialiasing.None;
        public TemporalAntialiasing temporalAntialiasing;
        public SubpixelMorphologicalAntialiasing subpixelMorphologicalAntialiasing;
        public FastApproximateAntialiasing fastApproximateAntialiasing;
        public Fog fog;
        public Dithering dithering;

        public PostProcessDebugLayer debugLayer;

        [SerializeField]
        PostProcessResources m_Resources;

        // UI states
        [SerializeField] bool m_ShowToolkit;
        [SerializeField] bool m_ShowCustomSorter;

        // Will stop applying post-processing effects just before color grading is applied
        // Currently used to export to exr without color grading
        public bool breakBeforeColorGrading = false;

        // Pre-ordered custom user effects
        // These are automatically populated and made to work properly with the serialization
        // system AND the editor. Modify at your own risk.
        [Serializable]
        public sealed class SerializedBundleRef
        {
            // We can't serialize Type so use assemblyQualifiedName instead, we only need this at
            // init time anyway so it's fine
            public string assemblyQualifiedName;

            // Not serialized, is set/reset when deserialization kicks in
            public PostProcessBundle bundle;
        }

        [SerializeField]
        List<SerializedBundleRef> m_BeforeTransparentBundles;

        [SerializeField]
        List<SerializedBundleRef> m_BeforeStackBundles;

        public Dictionary<PostProcessEvent, List<SerializedBundleRef>> sortedBundles { get; private set; }

        // We need to keep track of bundle initialization because for some obscure reason, on
        // assembly reload a MonoBehavior's Editor OnEnable will be called BEFORE the MonoBehavior's
        // own OnEnable... So we'll use it to pre-init bundles if the layer inspector is opened and
        // the component hasn't been enabled yet.
        public bool haveBundlesBeenInited { get; private set; }

        // Settings/Renderer bundles mapped to settings types
        Dictionary<Type, PostProcessBundle> m_Bundles;

        PropertySheetFactory m_PropertySheetFactory;
        CommandBuffer cBufferBeforeReflections;
        CommandBuffer cBufferBeforeLighting;
        CommandBuffer cBufferOpaque;
        CommandBuffer cBufferTAA;
        CommandBuffer cBufferBeforeStack;
        CommandBuffer cBufferStack;

        Camera m_Camera;
        PostProcessRenderContext currentContext;
        LogHistogram m_LogHistogram;

        bool m_SettingsUpdateNeeded = true;
        bool m_IsRenderingInSceneView = false;

        TargetPool m_TargetPool;

        bool NaNKilled = false;

        // Recycled list - used to reduce GC stress when gathering active effects in a bundle list
        // on each frame
        readonly List<PostProcessEffectRenderer> m_ActiveEffects = new List<PostProcessEffectRenderer>();
        readonly List<RenderTargetIdentifier> m_Targets = new List<RenderTargetIdentifier>();

#if UNITY_EDITOR
        private void OnValidate()
        {
            Shader.SetGlobalFloat("_Saturation", 1f);
        }
#endif

        void OnEnable()
        {
            Init(null);

            if (!haveBundlesBeenInited)
                InitBundles();

            m_LogHistogram = new LogHistogram();
            m_PropertySheetFactory = new PropertySheetFactory();
            m_TargetPool = new TargetPool();

            debugLayer.OnEnable();

            if (RuntimeUtilities.scriptableRenderPipelineActive)
                return;

            InitLegacy();
        }

        void InitLegacy()
        {
            cBufferBeforeReflections = new CommandBuffer { name = "Deferred Ambient Occlusion" };
            cBufferBeforeLighting = new CommandBuffer { name = "Deferred Ambient Occlusion" };
            cBufferOpaque = new CommandBuffer { name = "Opaque Only Post-processing" };
            cBufferBeforeStack = new CommandBuffer { name = "Post-processing" };
            cBufferStack = new CommandBuffer { name = "Stack" };
            cBufferTAA = new CommandBuffer { name = "Temporal AA" };

            m_Camera = GetComponent<Camera>();
            m_Camera.forceIntoRenderTexture = true; // Needed when running Forward / LDR / No MSAA
            m_Camera.AddCommandBuffer(CameraEvent.BeforeReflections, cBufferBeforeReflections);
            m_Camera.AddCommandBuffer(CameraEvent.BeforeLighting, cBufferBeforeLighting);
            m_Camera.AddCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, cBufferOpaque);
            m_Camera.AddCommandBuffer(CameraEvent.AfterImageEffectsOpaque, cBufferTAA);
            m_Camera.AddCommandBuffer(CameraEvent.BeforeImageEffects, cBufferBeforeStack);
            m_Camera.AddCommandBuffer(CameraEvent.BeforeImageEffects, cBufferStack);

            // Internal context used if no SRP is set
            currentContext = new PostProcessRenderContext();
        }

        public void Init(PostProcessResources resources)
        {
            if (resources != null) m_Resources = resources;

            RuntimeUtilities.CreateIfNull(ref temporalAntialiasing);
            RuntimeUtilities.CreateIfNull(ref subpixelMorphologicalAntialiasing);
            RuntimeUtilities.CreateIfNull(ref fastApproximateAntialiasing);
            RuntimeUtilities.CreateIfNull(ref dithering);
            RuntimeUtilities.CreateIfNull(ref fog);
            RuntimeUtilities.CreateIfNull(ref debugLayer);
        }

        public void InitBundles()
        {
            if (haveBundlesBeenInited)
                return;

            // Create these lists only once, the serialization system will take over after that
            RuntimeUtilities.CreateIfNull(ref m_BeforeTransparentBundles);
            RuntimeUtilities.CreateIfNull(ref m_BeforeStackBundles);

            // Create a bundle for each effect type
            m_Bundles = new Dictionary<Type, PostProcessBundle>();

            foreach (var type in PostProcessManager.instance.settingsTypes.Keys)
            {
                var settings = (PostProcessEffectSettings)ScriptableObject.CreateInstance(type);
                var bundle = new PostProcessBundle(settings);
                m_Bundles.Add(type, bundle);
            }

            // Update sorted lists with newly added or removed effects in the assemblies
            UpdateBundleSortList(m_BeforeTransparentBundles, PostProcessEvent.BeforeTransparent);
            UpdateBundleSortList(m_BeforeStackBundles, PostProcessEvent.BeforeStack);

            // Push all sorted lists in a dictionary for easier access
            sortedBundles = new Dictionary<PostProcessEvent, List<SerializedBundleRef>>(new PostProcessEventComparer())
            {
                { PostProcessEvent.BeforeTransparent, m_BeforeTransparentBundles },
                { PostProcessEvent.BeforeStack,       m_BeforeStackBundles }
            };

            // Done
            haveBundlesBeenInited = true;
        }

        void UpdateBundleSortList(List<SerializedBundleRef> sortedList, PostProcessEvent evt)
        {
            // First get all effects associated with the injection point
            var effects = m_Bundles.Where(kvp => kvp.Value.attribute.eventType == evt && !kvp.Value.attribute.builtinEffect)
                                   .Select(kvp => kvp.Value)
                                   .ToList();

            // Remove types that don't exist anymore
            sortedList.RemoveAll(x =>
            {
                string searchStr = x.assemblyQualifiedName;
                return !effects.Exists(b => b.settings.GetType().AssemblyQualifiedName == searchStr);
            });

            // Add new ones
            foreach (var effect in effects)
            {
                string typeName = effect.settings.GetType().AssemblyQualifiedName;

                if (!sortedList.Exists(b => b.assemblyQualifiedName == typeName))
                {
                    var sbr = new SerializedBundleRef { assemblyQualifiedName = typeName };
                    sortedList.Add(sbr);
                }
            }

            // Link internal references
            foreach (var effect in sortedList)
            {
                string typeName = effect.assemblyQualifiedName;
                var bundle = effects.Find(b => b.settings.GetType().AssemblyQualifiedName == typeName);
                effect.bundle = bundle;
            }
        }

        void OnDisable()
        {
            if (!RuntimeUtilities.scriptableRenderPipelineActive)
            {
                m_Camera.RemoveCommandBuffer(CameraEvent.BeforeReflections, cBufferBeforeReflections);
                m_Camera.RemoveCommandBuffer(CameraEvent.BeforeLighting, cBufferBeforeLighting);
                m_Camera.RemoveCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, cBufferOpaque);
                m_Camera.RemoveCommandBuffer(CameraEvent.AfterImageEffectsOpaque, cBufferTAA);
                m_Camera.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, cBufferBeforeStack);
                m_Camera.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, cBufferStack);
            }

            temporalAntialiasing.Release();
            m_LogHistogram.Release();

            foreach (var bundle in m_Bundles.Values)
                bundle.Release();

            m_Bundles.Clear();
            m_PropertySheetFactory.Release();

            if (debugLayer != null)
                debugLayer.OnDisable();

            // Might be an issue if several layers are blending in the same frame...
            TextureLerper.instance.Clear();

            haveBundlesBeenInited = false;
            loaded = false;
        }

        private void Update()
        {
            if (antialiasingMode == PostProcessLayer.Antialiasing.TemporalAntialiasing)
                temporalAntialiasing.UpdateJitterOffset();
        }

        // Called everytime the user resets the component from the inspector and more importantly
        // the first time it's added to a GameObject. As we don't have added/removed event for
        // components, this will do fine
        void Reset()
        {
            volumeTrigger = transform;
            //if (m_Camera.commandBufferCount > 0)
            //{
            //    m_Camera.RemoveAllCommandBuffers();
            //    loaded = false;

            //    BuildCommandBuffers();
            //}
        }

        void OnPreCull()
        {
            // Unused in scriptable render pipelines
            if (RuntimeUtilities.scriptableRenderPipelineActive)
                return;

            if (m_Camera == null || currentContext == null)
                InitLegacy();

            // Resets the projection matrix from previous frame in case TAA was enabled.
            // We also need to force reset the non-jittered projection matrix here as it's not done
            // when ResetProjectionMatrix() is called and will break transparent rendering if TAA
            // is switched off and the FOV or any other camera property changes.

#if UNITY_2018_2_OR_NEWER
            if (!m_Camera.usePhysicalProperties)
#endif
                if (antialiasingMode == PostProcessLayer.Antialiasing.TemporalAntialiasing)
                {
                    m_Camera.ResetProjectionMatrix();
                    m_Camera.nonJitteredProjectionMatrix = m_Camera.projectionMatrix;
                }

#if !UNITY_SWITCH
            if (m_Camera.stereoEnabled)
            {
                m_Camera.ResetStereoProjectionMatrices();
                Shader.SetGlobalFloat(ShaderIDs.RenderViewportScaleFactor, XRSettings.renderViewportScale);
            }
            else
#endif
            {
                Shader.SetGlobalFloat(ShaderIDs.RenderViewportScaleFactor, 1.0f);
            }
            BuildCommandBuffers();

        }

        void OnPreRender()
        {
            // Unused in scriptable render pipelines
            // Only needed for multi-pass stereo right eye
            if (RuntimeUtilities.scriptableRenderPipelineActive ||
                (m_Camera.stereoActiveEye != Camera.MonoOrStereoscopicEye.Right))
                return;
            BuildCommandBuffers();
        }

        bool loaded = false;
        RenderTextureFormat sourceFormat = RenderTextureFormat.DefaultHDR;
        RenderTargetIdentifier cameraTarget;
        void BuildCommandBuffers()
        {
            if (!loaded)
                LoadCommandBuffers();
            else
                UpdateCommandBuffers();
            m_TargetPool.Reset();
        }

        void LoadCommandBuffers()
        {
            var context = currentContext;
            NaNKilled = false;
            m_SettingsUpdateNeeded = true;

            context.Reset();
            context.camera = m_Camera;
            context.sourceFormat = sourceFormat;
            cameraTarget = new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget);

            SetupContext(context);

            if (currentContext.resources == null)
            {
                Debug.LogError("Resources has not been assigned to Readout Post Process Layer");
                return;
            }

            context.command = cBufferOpaque;
            UpdateSettingsIfNeeded(context);

            BuildDeferredAmbientOcclusion(context);
            BuildBeforeOpaqueEffects(context);
            if (antialiasingMode == PostProcessLayer.Antialiasing.TemporalAntialiasing)
                BuildTemporalAntialising(context);
            int lastTarget = BuildBeforeStack(context);
            BuildStack(context, lastTarget);

            TextureLerper.instance.EndFrame();
            loaded = true;
        }

        void UpdateCommandBuffers()
        {
            var context = currentContext;

            NaNKilled = false;

            //Custom grain update
            var grain = GetBundle<Grain>();
            if (grain.settings.enabled)
                ((GrainRenderer)grain.renderer).UpdateGrain(context);

            //Custom vignette update
            var vignette = GetBundle<Vignette>();
            if (vignette.settings.enabled)
                ((VignetteRenderer)vignette.renderer).UpdateVignette(context);

            context.command = cBufferTAA;
            context.camera = m_Camera;
            TextureLerper.instance.BeginFrame(context);

            if (antialiasingMode == PostProcessLayer.Antialiasing.TemporalAntialiasing)
                BuildTemporalAntialising(context);
            int lastTarget = BuildBeforeStack(context);

            if (lastTarget > -1)
                context.command.ReleaseTemporaryRT(lastTarget);

            TextureLerper.instance.EndFrame();
        }

        void BuildDeferredAmbientOcclusion(PostProcessRenderContext context)
        {
            TextureLerper.instance.BeginFrame(context);
            //Clean command buffers
            cBufferBeforeReflections.Clear();
            cBufferBeforeLighting.Clear();

            var aoBundle = GetBundle<AmbientOcclusion>();
            var aoRenderer = aoBundle.CastRenderer<AmbientOcclusionRenderer>();
            var ao = aoRenderer.Get();

            // Render as soon as possible - should be done async in SRPs when available
            context.command = cBufferBeforeReflections;

            ao.RenderAmbientOnly(context);

            // Composite with GBuffer right before the lighting pass
            context.command = cBufferBeforeLighting;
            ao.CompositeAmbientOnly(context);
        }

        void BuildBeforeOpaqueEffects(PostProcessRenderContext context)
        {
            var ssrBundle = GetBundle<ScreenSpaceReflections>();
            var ssrSettings = ssrBundle.settings;
            var ssrRenderer = ssrBundle.renderer;
            bool isScreenSpaceReflectionsActive = ssrSettings.IsEnabledAndSupported(context);


            bool isFogActive = fog.IsEnabledAndSupported(context);
            bool hasCustomOpaqueOnlyEffects = HasOpaqueOnlyEffects(context);
            int opaqueOnlyEffects = 0;
            opaqueOnlyEffects += isScreenSpaceReflectionsActive ? 1 : 0;
            opaqueOnlyEffects += isFogActive ? 1 : 0;
            opaqueOnlyEffects += hasCustomOpaqueOnlyEffects ? 1 : 0;

            // This works on right eye because it is resolved/populated at runtime

            if (opaqueOnlyEffects > 0)
            {
                var cmd = cBufferOpaque;
                context.command = cmd;

                // We need to use the internal Blit method to copy the camera target or it'll fail
                // on tiled GPU as it won't be able to resolve
                int tempTarget0 = m_TargetPool.Get();
                context.GetScreenSpaceTemporaryRT(cmd, tempTarget0, 0, sourceFormat);

                if (!NaNKilled)
                    NaNKilled = stopNaNPropagation;

                cmd.BuiltinBlit(cameraTarget, tempTarget0, RuntimeUtilities.copyStdMaterial, !NaNKilled && stopNaNPropagation ? 1 : 0);
                context.source = tempTarget0;

                int tempTarget1 = -1;

                if (opaqueOnlyEffects > 1)
                {
                    tempTarget1 = m_TargetPool.Get();
                    context.GetScreenSpaceTemporaryRT(cmd, tempTarget1, 0, sourceFormat);
                    context.destination = tempTarget1;
                }
                else context.destination = cameraTarget;

                if (isScreenSpaceReflectionsActive)
                {
                    ssrRenderer.Render(context);
                    opaqueOnlyEffects--;
                    var prevSource = context.source;
                    context.source = context.destination;
                    context.destination = opaqueOnlyEffects == 1 ? cameraTarget : prevSource;
                }

                if (isFogActive)
                {
                    fog.Render(context);
                    opaqueOnlyEffects--;
                    var prevSource = context.source;
                    context.source = context.destination;
                    context.destination = opaqueOnlyEffects == 1 ? cameraTarget : prevSource;
                }

                if (hasCustomOpaqueOnlyEffects)
                    RenderOpaqueOnly(context);

                if (opaqueOnlyEffects > 1)
                    cmd.ReleaseTemporaryRT(tempTarget1);

                cmd.ReleaseTemporaryRT(tempTarget0);
            }
        }

        void BuildTemporalAntialising(PostProcessRenderContext context)
        {
            cBufferTAA.Clear();

            // Do temporal anti-aliasing first
            if (!context.IsTemporalAntialiasingActive())
                return;

            var finalDestination = context.destination;

            // Post-transparency stack
            // Same as before, first blit needs to use the builtin Blit command to properly handle
            // tiled GPUs
            int tempRt = m_TargetPool.Get();
            context.GetScreenSpaceTemporaryRT(cBufferTAA, tempRt, 0, sourceFormat, RenderTextureReadWrite.sRGB);

            if (!NaNKilled)
                NaNKilled = stopNaNPropagation;

            cBufferTAA.BuiltinBlit(context.destination, tempRt, RuntimeUtilities.copyStdMaterial, !NaNKilled && stopNaNPropagation ? 1 : 0);

            context.command = cBufferTAA;
            context.source = tempRt;
            context.destination = finalDestination;

            temporalAntialiasing.ConfigureJitteredProjectionMatrix(context);
            temporalAntialiasing.Render(context);

            cBufferTAA.ReleaseTemporaryRT(tempRt);
        }

        int BuildBeforeStack(PostProcessRenderContext context)
        {
            //Clear buffer
            cBufferBeforeStack.Clear();

            int lastTarget = m_TargetPool.Get();
            context.GetScreenSpaceTemporaryRT(cBufferBeforeStack, lastTarget, 0, sourceFormat, RenderTextureReadWrite.sRGB);

            if (!NaNKilled)
                NaNKilled = stopNaNPropagation;

            cBufferBeforeStack.BuiltinBlit(cameraTarget, lastTarget, RuntimeUtilities.copyStdMaterial, !NaNKilled && stopNaNPropagation ? 1 : 0);

            context.command = cBufferBeforeStack;
            context.source = lastTarget;
            TextureLerper.instance.BeginFrame(context);

            if (HasActiveEffects(PostProcessEvent.BeforeStack, context))
            {
                lastTarget = RenderInjectionPoint(PostProcessEvent.BeforeStack, context, "BeforeStack", lastTarget);
            }

            return lastTarget;
        }

        void BuildStack(PostProcessRenderContext context, int lastTarget)
        {
            cBufferStack.Clear();
            SetupContext(context);

            var tempRt = lastTarget < 0 ? m_TargetPool.Get() : lastTarget;
            context.GetScreenSpaceTemporaryRT(cBufferStack, tempRt, 0, context.sourceFormat);

            context.command = cBufferStack;
            context.source = tempRt;
            context.destination = cameraTarget;


            // Builtin stack
            lastTarget = RenderBuiltins(context, true, tempRt);         //True because this is final pass

            if (lastTarget > -1)
                context.command.ReleaseTemporaryRT(lastTarget);
        }

        void OnPostRender()
        {
            // Unused in scriptable render pipelines
            if (RuntimeUtilities.scriptableRenderPipelineActive)
                return;

            if (currentContext.IsTemporalAntialiasingActive())
            {
#if UNITY_2018_2_OR_NEWER
                // TAA calls SetProjectionMatrix so if the camera projection mode was physical, it gets set to explicit. So we set it back to physical.
                if (currentContext.physicalCamera)
                    m_Camera.usePhysicalProperties = true;
                else
#endif
                    m_Camera.ResetProjectionMatrix();

                if (currentContext.stereoActive)
                {
                    if (RuntimeUtilities.isSinglePassStereoEnabled || m_Camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right)
                        m_Camera.ResetStereoProjectionMatrices();
                }
            }
        }

        public PostProcessBundle GetBundle<T>()
            where T : PostProcessEffectSettings
        {
            return GetBundle(typeof(T));
        }

        public PostProcessBundle GetBundle(Type settingsType)
        {
            Assert.IsTrue(m_Bundles.ContainsKey(settingsType), "Invalid type");
            return m_Bundles[settingsType];
        }

        public T GetSettings<T>()
            where T : PostProcessEffectSettings
        {
            return GetBundle<T>().CastSettings<T>();
        }

        public void BakeMSVOMap(CommandBuffer cmd, Camera camera, RenderTargetIdentifier destination, RenderTargetIdentifier? depthMap, bool invert, bool isMSAA = false)
        {
            var bundle = GetBundle<AmbientOcclusion>();
            var renderer = bundle.CastRenderer<AmbientOcclusionRenderer>().GetMultiScaleVO();
            renderer.SetResources(m_Resources);
            renderer.GenerateAOMap(cmd, camera, destination, depthMap, invert, isMSAA);
        }

        internal void OverrideSettings(List<PostProcessEffectSettings> baseSettings, float interpFactor)
        {
            // Go through all settings & overriden parameters for the given volume and lerp values
            foreach (var settings in baseSettings)
            {
                if (!settings.active)
                    continue;

                var target = GetBundle(settings.GetType()).settings;
                int count = settings.parameters.Count;

                for (int i = 0; i < count; i++)
                {
                    var toParam = settings.parameters[i];
                    if (toParam.overrideState)
                    {
                        var fromParam = target.parameters[i];
                        fromParam.Interp(fromParam, toParam, interpFactor);
                    }
                }
            }
        }

        // In the legacy render loop you have to explicitely set flags on camera to tell that you
        // need depth, depth+normals or motion vectors... This won't have any effect with most
        // scriptable render pipelines.
        void SetLegacyCameraFlags(PostProcessRenderContext context)
        {
            var flags = context.camera.depthTextureMode;

            foreach (var bundle in m_Bundles)
            {
                if (bundle.Value.settings.IsEnabledAndSupported(context))
                    flags |= bundle.Value.renderer.GetCameraFlags();
            }

            // Special case for AA & lighting effects
            if (context.IsTemporalAntialiasingActive())
                flags |= temporalAntialiasing.GetCameraFlags();

            if (fog.IsEnabledAndSupported(context))
                flags |= fog.GetCameraFlags();

            if (debugLayer.debugOverlay != DebugOverlay.None)
                flags |= debugLayer.GetCameraFlags();

            context.camera.depthTextureMode = flags;
        }

        // Call this function whenever you need to reset any temporal effect (TAA, Motion Blur etc).
        // Mainly used when doing camera cuts.
        public void ResetHistory()
        {
            foreach (var bundle in m_Bundles)
                bundle.Value.ResetHistory();

            temporalAntialiasing.ResetHistory();
        }

        public bool HasOpaqueOnlyEffects(PostProcessRenderContext context)
        {
            return HasActiveEffects(PostProcessEvent.BeforeTransparent, context);
        }

        public bool HasActiveEffects(PostProcessEvent evt, PostProcessRenderContext context)
        {
            var list = sortedBundles[evt];

            foreach (var item in list)
            {
                if (item.bundle.settings.IsEnabledAndSupported(context))
                    return true;
            }

            return false;
        }

        void SetupContext(PostProcessRenderContext context)
        {
			RuntimeUtilities.UpdateResources(m_Resources);

			m_IsRenderingInSceneView = context.camera.cameraType == CameraType.SceneView;
            context.isSceneView = m_IsRenderingInSceneView;
            context.resources = m_Resources;
            context.propertySheets = m_PropertySheetFactory;
            context.debugLayer = debugLayer;
            context.antialiasing = antialiasingMode;
            context.temporalAntialiasing = temporalAntialiasing;
            context.logHistogram = m_LogHistogram;

#if UNITY_2018_2_OR_NEWER
            context.physicalCamera = context.camera.usePhysicalProperties;
#endif

            SetLegacyCameraFlags(context);

            // Prepare debug overlay
            debugLayer.SetFrameSize(context.width, context.height);

            // Unsafe to keep this around but we need it for OnGUI events for debug views
            // Will be removed eventually
            currentContext = context;
        }

        void UpdateSettingsIfNeeded(PostProcessRenderContext context)
        {
            if (m_SettingsUpdateNeeded)
            {
                context.command.BeginSample("VolumeBlending");
                PostProcessManager.instance.UpdateSettings(this, context.camera);
                context.command.EndSample("VolumeBlending");
                m_TargetPool.Reset();

                // TODO: fix me once VR support is in SRP
                // Needed in SRP so that _RenderViewportScaleFactor isn't 0
                if (RuntimeUtilities.scriptableRenderPipelineActive)
                    Shader.SetGlobalFloat(ShaderIDs.RenderViewportScaleFactor, 1f);
            }

            m_SettingsUpdateNeeded = false;
        }

        // Renders before-transparent effects.
        // Make sure you check `HasOpaqueOnlyEffects()` before calling this method as it won't
        // automatically blit source into destination if no opaque effects are active.
        public void RenderOpaqueOnly(PostProcessRenderContext context)
        {
            if (RuntimeUtilities.scriptableRenderPipelineActive)
                SetupContext(context);

            TextureLerper.instance.BeginFrame(context);

            // Update & override layer settings first (volume blending), will only be done once per
            // frame, either here or in Render() if there isn't any opaque-only effect to render.
            UpdateSettingsIfNeeded(context);

            RenderList(sortedBundles[PostProcessEvent.BeforeTransparent], context, "OpaqueOnly");
        }

        // Renders everything not opaque-only
        //
        // Current order of operation is as following:
        //     1. Pre-stack
        //     2. Built-in stack
        //     3. Post-stack
        //     4. Built-in final pass
        //
        // Final pass should be skipped when outputting to a HDR display.
        public void Render(PostProcessRenderContext context)
        {
            if (RuntimeUtilities.scriptableRenderPipelineActive)
                SetupContext(context);

            TextureLerper.instance.BeginFrame(context);
            var cmd = context.command;

            // Update & override layer settings first (volume blending) if the opaque only pass
            // hasn't been called this frame.
            UpdateSettingsIfNeeded(context);

            // Do a NaN killing pass if needed
            int lastTarget = -1;
            if (stopNaNPropagation && !NaNKilled)
            {
                lastTarget = m_TargetPool.Get();
                context.GetScreenSpaceTemporaryRT(cmd, lastTarget, 0, context.sourceFormat);
                cmd.BlitFullscreenTriangle(context.source, lastTarget, RuntimeUtilities.copySheet, 1);
                context.source = lastTarget;
                NaNKilled = true;
            }

            // Do temporal anti-aliasing first
            if (context.IsTemporalAntialiasingActive())
            {
                if (!RuntimeUtilities.scriptableRenderPipelineActive)
                {
                    if (context.stereoActive)
                    {
                        // We only need to configure all of this once for stereo, during OnPreCull
                        if (context.camera.stereoActiveEye != Camera.MonoOrStereoscopicEye.Right)
                            temporalAntialiasing.ConfigureStereoJitteredProjectionMatrices(context);
                    }
                    else
                    {
                        temporalAntialiasing.ConfigureJitteredProjectionMatrix(context);
                    }
                }

                var taaTarget = m_TargetPool.Get();
                var finalDestination = context.destination;
                context.GetScreenSpaceTemporaryRT(cmd, taaTarget, 0, context.sourceFormat);
                context.destination = taaTarget;
                temporalAntialiasing.Render(context);
                context.source = taaTarget;
                context.destination = finalDestination;

                if (lastTarget > -1)
                    cmd.ReleaseTemporaryRT(lastTarget);

                lastTarget = taaTarget;
            }

            bool hasBeforeStackEffects = HasActiveEffects(PostProcessEvent.BeforeStack, context);

            // Right before the builtin stack
            if (hasBeforeStackEffects)
                lastTarget = RenderInjectionPoint(PostProcessEvent.BeforeStack, context, "BeforeStack", lastTarget);


            var cameraTarget = new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget);

            if (loaded)
            {
                var tempRt = lastTarget < 0 ? m_TargetPool.Get() : lastTarget;
                context.GetScreenSpaceTemporaryRT(cBufferStack, tempRt, 0, context.sourceFormat, RenderTextureReadWrite.sRGB);

                context.command = cBufferBeforeStack;
                context.source = tempRt;
                context.destination = cameraTarget;

                context.command = cBufferStack;

                // Builtin stack
                lastTarget = RenderBuiltins(context, true, tempRt);
            }

            // Render debug monitors & overlay if requested
            debugLayer.RenderSpecialOverlays(context);
            debugLayer.RenderMonitors(context);

            // End frame cleanup
            TextureLerper.instance.EndFrame();
            debugLayer.EndFrame();
            m_SettingsUpdateNeeded = true;
            NaNKilled = false;
        }

        int RenderInjectionPoint(PostProcessEvent evt, PostProcessRenderContext context, string marker, int releaseTargetAfterUse = -1)
        {
            int tempTarget = m_TargetPool.Get();
            var finalDestination = context.destination;

            var cmd = context.command;
            context.GetScreenSpaceTemporaryRT(cmd, tempTarget, 0, context.sourceFormat);
            context.destination = tempTarget;
            RenderList(sortedBundles[evt], context, marker);
            context.source = tempTarget;
            context.destination = finalDestination;

            if (releaseTargetAfterUse > -1)
                cmd.ReleaseTemporaryRT(releaseTargetAfterUse);

            return tempTarget;
        }

        void RenderList(List<SerializedBundleRef> list, PostProcessRenderContext context, string marker)
        {
            var cmd = context.command;
            cmd.BeginSample(marker);

            // First gather active effects - we need this to manage render targets more efficiently
            m_ActiveEffects.Clear();
            for (int i = 0; i < list.Count; i++)
            {
                var effect = list[i].bundle;
                if (effect.settings.IsEnabledAndSupported(context))
                {
                    if (!context.isSceneView || (context.isSceneView && effect.attribute.allowInSceneView))
                        m_ActiveEffects.Add(effect.renderer);
                }
            }

            int count = m_ActiveEffects.Count;

            // If there's only one active effect, we can simply execute it and skip the rest
            if (count == 1)
            {
                m_ActiveEffects[0].Render(context);
            }
            else
            {
                // Else create the target chain
                m_Targets.Clear();
                m_Targets.Add(context.source); // First target is always source

                int tempTarget1 = m_TargetPool.Get();
                int tempTarget2 = m_TargetPool.Get();

                for (int i = 0; i < count - 1; i++)
                    m_Targets.Add(i % 2 == 0 ? tempTarget1 : tempTarget2);

                m_Targets.Add(context.destination); // Last target is always destination

                // Render
                context.GetScreenSpaceTemporaryRT(cmd, tempTarget1, 0, context.sourceFormat);
                if (count > 2)
                    context.GetScreenSpaceTemporaryRT(cmd, tempTarget2, 0, context.sourceFormat);

                for (int i = 0; i < count; i++)
                {
                    context.source = m_Targets[i];
                    context.destination = m_Targets[i + 1];
                    m_ActiveEffects[i].Render(context);
                }

                cmd.ReleaseTemporaryRT(tempTarget1);
                if (count > 2)
                    cmd.ReleaseTemporaryRT(tempTarget2);
            }

            cmd.EndSample(marker);
        }

        void ApplyFlip(PostProcessRenderContext context, MaterialPropertyBlock properties)
        {
            if (context.flip && !context.isSceneView)
                properties.SetVector(ShaderIDs.UVTransform, new Vector4(1.0f, 1.0f, 0.0f, 0.0f));
            else
                ApplyDefaultFlip(properties);
        }

        void ApplyDefaultFlip(MaterialPropertyBlock properties)
        {
            properties.SetVector(ShaderIDs.UVTransform, SystemInfo.graphicsUVStartsAtTop ? new Vector4(1.0f, -1.0f, 0.0f, 1.0f) : new Vector4(1.0f, 1.0f, 0.0f, 0.0f));
        }


        int RenderBuiltins(PostProcessRenderContext context, bool isFinalPass, int releaseTargetAfterUse = -1)
        {
            var uberSheet = context.propertySheets.Get(context.resources.shaders.uber);
            uberSheet.ClearKeywords();
            uberSheet.properties.Clear();
            context.uberSheet = uberSheet;
            context.autoExposureTexture = RuntimeUtilities.whiteTexture;
            context.bloomBufferNameID = -1;

            var cmd = context.command;
            cmd.BeginSample("BuiltinStack");

            int tempTarget = -1;
            var finalDestination = context.destination;

            if (!isFinalPass)
            {
                // Render to an intermediate target as this won't be the final pass
                tempTarget = m_TargetPool.Get();
                context.GetScreenSpaceTemporaryRT(cmd, tempTarget, 0, context.sourceFormat);
                context.destination = tempTarget;

                // Handle FXAA's keep alpha mode
                if (antialiasingMode == PostProcessLayer.Antialiasing.FastApproximateAntialiasing && !fastApproximateAntialiasing.keepAlpha)
                    uberSheet.properties.SetFloat(ShaderIDs.LumaInAlpha, 1f);
            }

            // Depth of field final combination pass used to be done in Uber which led to artifacts
            // when used at the same time as Bloom (because both effects used the same source, so
            // the stronger bloom was, the more DoF was eaten away in out of focus areas)
            int depthOfFieldTarget = RenderEffect<DepthOfField>(context, true);

            // Motion blur is a separate pass - could potentially be done after DoF depending on the
            // kind of results you're looking for...
            int motionBlurTarget = RenderEffect<MotionBlur>(context, true);

            // Prepare exposure histogram if needed
            if (ShouldGenerateLogHistogram(context))
                m_LogHistogram.Generate(context);

            // Uber effects
            RenderEffect<AutoExposure>(context);
            uberSheet.properties.SetTexture(ShaderIDs.AutoExposureTex, context.autoExposureTexture);

            RenderEffect<LensDistortion>(context);
            RenderEffect<ChromaticAberration>(context);
            RenderEffect<Bloom>(context);
            RenderEffect<Vignette>(context);
            RenderEffect<Grain>(context);

            if (!breakBeforeColorGrading)
                RenderEffect<ColorGrading>(context);

            if (isFinalPass)
            {
                uberSheet.EnableKeyword("FINALPASS");
                dithering.Render(context);
                ApplyFlip(context, uberSheet.properties);
            }
            else
            {
                ApplyDefaultFlip(uberSheet.properties);
            }

            cmd.BlitFullscreenTriangle(context.source, context.destination, uberSheet, 0);

            context.source = context.destination;
            context.destination = finalDestination;

            if (releaseTargetAfterUse > -1) cmd.ReleaseTemporaryRT(releaseTargetAfterUse);
            if (motionBlurTarget > -1) cmd.ReleaseTemporaryRT(motionBlurTarget);
            if (depthOfFieldTarget > -1) cmd.ReleaseTemporaryRT(depthOfFieldTarget);
            if (context.bloomBufferNameID > -1) cmd.ReleaseTemporaryRT(context.bloomBufferNameID);

            cmd.EndSample("BuiltinStack");
            return tempTarget;
        }

        // This pass will have to be disabled for HDR screen output as it's an LDR pass
        void RenderFinalPass(PostProcessRenderContext context, int releaseTargetAfterUse = -1)
        {
            var cmd = context.command;
            cmd.BeginSample("FinalPass");

            if (breakBeforeColorGrading)
            {
                var sheet = context.propertySheets.Get(context.resources.shaders.discardAlpha);
                cmd.BlitFullscreenTriangle(context.source, context.destination, sheet, 0);
            }
            else
            {
                var uberSheet = context.propertySheets.Get(context.resources.shaders.finalPass);
                uberSheet.ClearKeywords();
                uberSheet.properties.Clear();
                context.uberSheet = uberSheet;
                int tempTarget = -1;

                if (antialiasingMode == PostProcessLayer.Antialiasing.FastApproximateAntialiasing)
                {
                    uberSheet.EnableKeyword(fastApproximateAntialiasing.fastMode
                        ? "FXAA_LOW"
                        : "FXAA"
                    );

                    if (fastApproximateAntialiasing.keepAlpha)
                        uberSheet.EnableKeyword("FXAA_KEEP_ALPHA");
                }
                else if (antialiasingMode == PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing && subpixelMorphologicalAntialiasing.IsSupported())
                {
                    tempTarget = m_TargetPool.Get();
                    var finalDestination = context.destination;
                    context.GetScreenSpaceTemporaryRT(context.command, tempTarget, 0, context.sourceFormat);
                    context.destination = tempTarget;
                    subpixelMorphologicalAntialiasing.Render(context);
                    context.source = tempTarget;
                    context.destination = finalDestination;
                }

                dithering.Render(context);

                ApplyFlip(context, uberSheet.properties);
                cmd.BlitFullscreenTriangle(context.source, context.destination, uberSheet, 0);

                if (tempTarget > -1)
                    cmd.ReleaseTemporaryRT(tempTarget);
            }

            if (releaseTargetAfterUse > -1)
                cmd.ReleaseTemporaryRT(releaseTargetAfterUse);

            cmd.EndSample("FinalPass");
        }

        int RenderEffect<T>(PostProcessRenderContext context, bool useTempTarget = false)
            where T : PostProcessEffectSettings
        {
            var effect = GetBundle<T>();

            if (!effect.settings.IsEnabledAndSupported(context))
                return -1;

            if (m_IsRenderingInSceneView && !effect.attribute.allowInSceneView)
                return -1;

            if (!useTempTarget)
            {
                effect.renderer.Render(context);
                return -1;
            }

            var finalDestination = context.destination;
            var tempTarget = m_TargetPool.Get();
            context.GetScreenSpaceTemporaryRT(context.command, tempTarget, 0, context.sourceFormat);
            context.destination = tempTarget;
            effect.renderer.Render(context);
            context.source = tempTarget;
            context.destination = finalDestination;
            return tempTarget;
        }

        bool ShouldGenerateLogHistogram(PostProcessRenderContext context)
        {
            bool autoExpo = GetBundle<AutoExposure>().settings.IsEnabledAndSupported(context);
            bool lightMeter = debugLayer.lightMeter.IsRequestedAndSupported(context);
            return autoExpo || lightMeter;
        }
    }
}