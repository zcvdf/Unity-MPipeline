﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using static Unity.Mathematics.math;
using Unity.Jobs;
using System.Threading;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.Rendering;
namespace MPipeline
{
    public unsafe sealed class MTerrain : JobProcessEvent
    {
        public static MTerrain current { get; private set; }
        public struct TerrainChunkBuffer
        {
            public float2 worldPos;
            public float2 minMaxHeight;
            public float2 scale;
            public uint2 uvStartIndex;
        }
        [System.Serializable]
        public struct PBRTexture
        {
            public AssetReference albedoOccTex;
            public AssetReference normalTex;
            public AssetReference SMTex;
        }
        public const int HEIGHT_RESOLUTION = 256;
        public const int COLOR_RESOLUTION = 1024;
        public float[] lodDistances = new float[]
        {
            1000,
            600,
            300
        };

        #region QUADTREE
        [System.NonSerialized]
        public bool terrainEnabled;
        public double largestChunkSize = 1000;
        public double2 screenOffset;
        public NativeList_Float allLodLevles;
        public float lodDeferredOffset = 2;
        public VirtualTextureLoader loader;
        public NativeList<TerrainLoadData> loadDataList;
        #endregion

        public string readWritePath = "Assets/BinaryData/Terrain.mquad";
        public int planarResolution = 10;
        public Material drawTerrainMaterial;
        public Transform cam;
        public PBRTexture[] textures;

        private ComputeBuffer culledResultsBuffer;
        private ComputeBuffer loadedBuffer;
        private ComputeBuffer dispatchDrawBuffer;
        private ComputeShader shader;
        private ComputeShader textureShader;

        private RenderTexture albedoTex;
        private RenderTexture normalTex;
        private RenderTexture smTex;

        private NativeList<TerrainChunkBuffer> loadedBufferList;
        private static Vector4[] planes = new Vector4[6];
        private TerrainQuadTree tree;
        private JobHandle calculateHandle;
        private MStringBuilder msb;
        private VirtualTexture vt;
        private struct TerrainLoadHandler
        {
            public AssetReference maskRefs;
            public AssetReference heightRefs;
            public int2 startIndex;
            public int size;
        }
        private List<TerrainLoadHandler> allCommands = new List<TerrainLoadHandler>(100);

        public override void PrepareJob()
        {
            loadedBufferList.Clear();
            calculateHandle = new CalculateQuadTree
            {
                tree = tree.Ptr(),
                cameraXZPos = double2(cam.position.x, cam.position.z),
                loadedBuffer = loadedBufferList,
            }.Schedule();
        }

        public override void FinishJob()
        {
            calculateHandle.Complete();
            vt.Update(drawTerrainMaterial);
            for (int i = 0; i < loadDataList.Length; ++i)
            {
                ref TerrainLoadData loadData = ref loadDataList[i];
                switch (loadData.ope)
                {
                    case TerrainLoadData.Operator.Combine:
                        vt.CombineTexture(loadData.startIndex, loadData.size);
                        break;
                    case TerrainLoadData.Operator.Unload:
                        vt.UnloadTexture(loadData.startIndex);
                        break;
                    case TerrainLoadData.Operator.Load:
                        TerrainLoadHandler handler;
                        loadData.targetLoadChunk.height.GetString(msb);
                        handler.heightRefs = new AssetReference(msb.str);
                        loadData.targetLoadChunk.mask.GetString(msb);
                        handler.maskRefs = new AssetReference(msb.str);
                        handler.size = loadData.size;
                        handler.startIndex = loadData.startIndex;
                        break;
                }
            }
            loadDataList.Clear();
            UpdateBuffer();
        }

        IEnumerator AsyncLoader()
        {
            textureShader.SetTexture(1, ShaderIDs._VirtualMainTex, albedoTex);
            textureShader.SetTexture(1, ShaderIDs._VirtualBumpMap, normalTex);
            textureShader.SetTexture(1, ShaderIDs._VirtualSMO, smTex);
            textureShader.SetTexture(3, ShaderIDs._VirtualMainTex, albedoTex);
            textureShader.SetTexture(3, ShaderIDs._VirtualBumpMap, normalTex);
            textureShader.SetTexture(3, ShaderIDs._VirtualSMO, smTex);
            for (int i = 0; i < textures.Length; ++i)
            {
                PBRTexture texs = textures[i];
                AsyncOperationHandle<Texture> albedoLoader = texs.albedoOccTex.LoadAssetAsync<Texture>();
                AsyncOperationHandle<Texture> normalLoader = texs.normalTex.LoadAssetAsync<Texture>();
                AsyncOperationHandle<Texture> smLoader = texs.SMTex.LoadAssetAsync<Texture>();
                yield return albedoLoader;
                yield return normalLoader;
                yield return smLoader;
                yield return null;
                const int disp = COLOR_RESOLUTION / 8;
                textureShader.SetInt(ShaderIDs._OffsetIndex, i);
                if (albedoLoader.Result && normalLoader.Result && smLoader.Result)
                {
                    textureShader.SetTexture(1, ShaderIDs._TerrainMainTexArray, albedoLoader.Result);
                    textureShader.SetTexture(1, ShaderIDs._TerrainBumpMapArray, normalLoader.Result);
                    textureShader.SetTexture(1, ShaderIDs._TerrainSMTexArray, smLoader.Result);
                    textureShader.Dispatch(1, disp, disp, 1);
                }
                else
                {
                    textureShader.Dispatch(3, disp, disp, 1);
                }
                texs.albedoOccTex.ReleaseAsset();
                texs.normalTex.ReleaseAsset();
                texs.SMTex.ReleaseAsset();
            }
            textureShader.SetInt(ShaderIDs._Count, textures.Length);
            while (enabled)
            {
                for (int i = 0; i < allCommands.Count; ++i)
                {
                    TerrainLoadHandler handler = allCommands[i];
                    var maskHandler = handler.maskRefs.LoadAssetAsync<Texture>();
                    var heightHandler = handler.heightRefs.LoadAssetAsync<Texture>();
                    yield return maskHandler;
                    yield return heightHandler;
                    yield return null;
                    Texture mask = maskHandler.Result;
                    int texElement = vt.LoadNewTexture(handler.startIndex, handler.size);
                    int colorPass;
                    if (mask)
                    {
                        colorPass = 0;
                        textureShader.SetTexture(0, ShaderIDs._SourceTex, mask);
                        textureShader.SetTexture(0, ShaderIDs._MainTex, albedoTex);
                        textureShader.SetTexture(0, ShaderIDs._BumpMap, normalTex);
                        textureShader.SetTexture(0, ShaderIDs._SMMap, smTex);
                    }
                    else colorPass = 3;
                    textureShader.SetTexture(colorPass, ShaderIDs._VirtualMainTex, vt.GetTexture(1));
                    textureShader.SetTexture(colorPass, ShaderIDs._VirtualBumpMap, vt.GetTexture(2));
                    textureShader.SetTexture(colorPass, ShaderIDs._VirtualSMO, vt.GetTexture(3));
                    textureShader.SetInt(ShaderIDs._OffsetIndex, texElement);
                    textureShader.SetVector(ShaderIDs._TextureSize, float4(mask.width, mask.height, COLOR_RESOLUTION, COLOR_RESOLUTION));
                    const int disp = COLOR_RESOLUTION / 8;
                    textureShader.Dispatch(colorPass, disp, disp, 1);
                    int heightPass;
                    if (heightHandler.Result)
                    {
                        heightPass = 2;
                        textureShader.SetTexture(2, ShaderIDs.heightMapBuffer, heightHandler.Result);
                    }
                    else heightPass = 4;
                    textureShader.SetTexture(heightPass, ShaderIDs._VirtualHeightmap, vt.GetTexture(0));
                    const int dispH = HEIGHT_RESOLUTION / 8;
                    textureShader.Dispatch(heightPass, dispH, dispH, 1);
                    handler.maskRefs.ReleaseAsset();
                    handler.heightRefs.ReleaseAsset();
                }
                allCommands.Clear();
                yield return null;
            }
        }

        protected override void OnEnableFunc()
        {
            terrainEnabled = true;
            msb = new MStringBuilder(32);
            textureShader = Resources.Load<ComputeShader>("ProceduralTexture");
            shader = Resources.Load<ComputeShader>("TerrainCompute");
            if (current && current != this)
            {
                enabled = false;
                Debug.LogError("Only One Terrain allowed!");
                return;
            }
            current = this;
            int indexMapSize = 1;
            for (int i = 1; i < lodDistances.Length; ++i)
            {
                indexMapSize *= 2;
            }
            dispatchDrawBuffer = new ComputeBuffer(5, sizeof(int), ComputeBufferType.IndirectArguments);
            const int INIT_LENGTH = 500;
            culledResultsBuffer = new ComputeBuffer(INIT_LENGTH, sizeof(int));
            loadedBuffer = new ComputeBuffer(INIT_LENGTH, sizeof(TerrainChunkBuffer));
            loadedBufferList = new NativeList<TerrainChunkBuffer>(INIT_LENGTH, Allocator.Persistent);
            loader = new VirtualTextureLoader(lodDistances.Length, readWritePath);
            loadDataList = new NativeList<TerrainLoadData>(100, Allocator.Persistent);
            Debug.Log("First");
            NativeArray<uint> dispatchDraw = new NativeArray<uint>(5, Allocator.Temp, NativeArrayOptions.ClearMemory);
            dispatchDraw[0] = 6;
            dispatchDrawBuffer.SetData(dispatchDraw);
            VirtualTextureFormat* formats = stackalloc VirtualTextureFormat[]
            {
                new VirtualTextureFormat((VirtualTextureSize)HEIGHT_RESOLUTION, RenderTextureFormat.R16, "_VirtualHeightmap"),
                new VirtualTextureFormat((VirtualTextureSize)COLOR_RESOLUTION, RenderTextureFormat.ARGB32, "_VirtualMainTex"),
                new VirtualTextureFormat((VirtualTextureSize)COLOR_RESOLUTION, RenderTextureFormat.RGHalf, "_VirtualBumpMap"),
                new VirtualTextureFormat((VirtualTextureSize)COLOR_RESOLUTION, RenderTextureFormat.RG16, "_VirtualSMMap")
            };
            vt = new VirtualTexture(128, min(2048, (int)(pow(2.0, lodDistances.Length) + 0.1)), formats, 4);
            allLodLevles = new NativeList_Float(lodDistances.Length, Allocator.Persistent);
            for (int i = 0; i < lodDistances.Length; ++i)
            {
                allLodLevles.Add(min(lodDistances[max(0, i - 1)], lodDistances[i]));
            }
            allLodLevles[lodDistances.Length] = 0;

            albedoTex = new RenderTexture(new RenderTextureDescriptor
            {
                colorFormat = RenderTextureFormat.ARGB32,
                dimension = TextureDimension.Tex2DArray,
                width = COLOR_RESOLUTION,
                height = COLOR_RESOLUTION,
                volumeDepth = Mathf.Max(1, textures.Length),
                enableRandomWrite = true,
                msaaSamples = 1
            });
            albedoTex.Create();
            normalTex = new RenderTexture(new RenderTextureDescriptor
            {
                colorFormat = RenderTextureFormat.RGHalf,
                dimension = TextureDimension.Tex2DArray,
                width = COLOR_RESOLUTION,
                height = COLOR_RESOLUTION,
                volumeDepth = Mathf.Max(1, textures.Length),
                enableRandomWrite = true,
                msaaSamples = 1
            });
            normalTex.Create();
            smTex = new RenderTexture(new RenderTextureDescriptor
            {
                colorFormat = RenderTextureFormat.RG16,
                dimension = TextureDimension.Tex2DArray,
                width = COLOR_RESOLUTION,
                height = COLOR_RESOLUTION,
                volumeDepth = Mathf.Max(1, textures.Length),
                enableRandomWrite = true,
                msaaSamples = 1
            });
            smTex.Create();
            tree = new TerrainQuadTree(-1, TerrainQuadTree.LocalPos.LeftDown, 0);
            StartCoroutine(AsyncLoader());
        }
        void UpdateBuffer()
        {
            if (!loadedBufferList.isCreated) return;
            if (loadedBufferList.Length > loadedBuffer.count)
            {
                loadedBuffer.Dispose();
                culledResultsBuffer.Dispose();
                loadedBuffer = new ComputeBuffer(loadedBufferList.Capacity, sizeof(TerrainChunkBuffer));
                culledResultsBuffer = new ComputeBuffer(loadedBufferList.Capacity, sizeof(int));
            }
            loadedBuffer.SetDataPtr(loadedBufferList.unsafePtr, loadedBufferList.Length);
        }

        public void DrawTerrain(CommandBuffer buffer, int pass, Vector4[] planes)
        {
            if (loadedBufferList.Length <= 0) return;
            buffer.SetComputeBufferParam(shader, 1, ShaderIDs._DispatchBuffer, dispatchDrawBuffer);
            buffer.SetComputeBufferParam(shader, 0, ShaderIDs._DispatchBuffer, dispatchDrawBuffer);
            buffer.SetComputeBufferParam(shader, 0, ShaderIDs._CullResultBuffer, culledResultsBuffer);
            buffer.SetComputeBufferParam(shader, 0, ShaderIDs._TerrainChunks, loadedBuffer);
            buffer.SetGlobalBuffer(ShaderIDs._TerrainChunks, loadedBuffer);
            buffer.SetGlobalBuffer(ShaderIDs._CullResultBuffer, culledResultsBuffer);
            buffer.SetComputeVectorArrayParam(shader, ShaderIDs.planes, planes);
            buffer.DispatchCompute(shader, 1, 1, 1, 1);
            ComputeShaderUtility.Dispatch(shader, buffer, 0, loadedBufferList.Length);
            buffer.DrawProceduralIndirect(Matrix4x4.identity, drawTerrainMaterial, pass, MeshTopology.Triangles, dispatchDrawBuffer);
        }

        public void DrawTerrain(CommandBuffer buffer, int pass, float4* planePtr)
        {
            UnsafeUtility.MemCpy(planes.Ptr(), planePtr, sizeof(float4) * 6);
            DrawTerrain(buffer, pass, planes);
        }

        protected override void OnDisableFunc()
        {
            if (current != this) return;
            current = null;
            terrainEnabled = false;
            if (culledResultsBuffer != null) culledResultsBuffer.Dispose();
            if (loadedBuffer != null) loadedBuffer.Dispose();
            if (dispatchDrawBuffer != null) dispatchDrawBuffer.Dispose();
            if (loadedBufferList.isCreated) loadedBufferList.Dispose();
            tree.Dispose();
            vt.Dispose();
            loadDataList.Dispose();
            loader.Dispose();
            allLodLevles.Dispose();
            allCommands.Clear();
            DestroyImmediate(albedoTex);
            DestroyImmediate(normalTex);
            DestroyImmediate(smTex);

        }

        private struct CalculateQuadTree : IJob
        {
            [NativeDisableUnsafePtrRestriction]
            public TerrainQuadTree* tree;
            public double2 cameraXZPos;
            public NativeList<TerrainChunkBuffer> loadedBuffer;

            public void Execute()
            {

                tree->CheckUpdate(cameraXZPos);
                tree->PushDrawRequest(loadedBuffer);
            }
        }
    }
}