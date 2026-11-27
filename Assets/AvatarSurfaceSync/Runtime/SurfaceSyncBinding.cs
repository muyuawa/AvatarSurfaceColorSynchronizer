using System;
using VRC.SDKBase;
using UnityEngine;

namespace AvatarSurfaceSync
{
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public sealed class SurfaceSyncBinding : MonoBehaviour, IEditorOnly
    {
        [Serializable]
        public struct Entry
        {
            public Renderer Renderer;
            public int Slot;
            public Material Original, Replacement;
            public Texture SourceTexture, BakedTexture;
            public Vector4 SourceUv;
        }

        [HideInInspector] public bool OwnsGameObject;
        public Entry[] Entries = Array.Empty<Entry>();
    }
}
