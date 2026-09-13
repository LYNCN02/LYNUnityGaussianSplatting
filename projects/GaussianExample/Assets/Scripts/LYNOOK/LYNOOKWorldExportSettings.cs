using UnityEngine;

namespace Lynook.DualScreen
{
    /// <summary>Optional scene settings for the world_config.json saved with each recording.</summary>
    [DisallowMultipleComponent]
    public sealed class LYNOOKWorldExportSettings : MonoBehaviour
    {
        [Tooltip("Optional frame for the supplied GLB's Unity coordinates. Cameras are exported relative to this frame; the runtime mesh stays at identity. Use positive uniform scale, without reflection or shear. Leave empty to use recording scene coordinates.")]
        public Transform worldCoordinateRoot;

        public string displayName;
        [Tooltip("Relative to the exported take folder. Supply this file separately; recording does not generate or copy a GLB.")]
        public string collisionMesh = "mesh/collision.glb";
        [Tooltip("Optional existing navigation file, relative to the take folder.")]
        public string gridMap = "nav/grid_map.json";
        public string preview = "preview.png";
        public string mainRendererName = "Screen_Main";
        public string sideRendererName = "Screen_Right";
    }
}
