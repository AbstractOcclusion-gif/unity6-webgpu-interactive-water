using NUnit.Framework;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    public sealed class TileUniformFeatureTests
    {
        static readonly int TilesProperty = Shader.PropertyToID("_Tiles");

        [Test]
        public void BodyPropertyBlocks_KeepDistinctPoolTileTextures()
        {
            GameObject firstObject = CreateInactiveVolume("First Tiles", out WaterVolume first);
            GameObject secondObject = CreateInactiveVolume("Second Tiles", out WaterVolume second);
            try
            {
                first.tiles = Texture2D.whiteTexture;
                second.tiles = Texture2D.blackTexture;
                var firstProperties = new MaterialPropertyBlock();
                var secondProperties = new MaterialPropertyBlock();

                new WaterUniformPublisher(first).WriteBodyProps(firstProperties);
                new WaterUniformPublisher(second).WriteBodyProps(secondProperties);

                Assert.That(firstProperties.GetTexture(TilesProperty), Is.EqualTo(Texture2D.whiteTexture));
                Assert.That(secondProperties.GetTexture(TilesProperty), Is.EqualTo(Texture2D.blackTexture));
            }
            finally
            {
                Object.DestroyImmediate(firstObject);
                Object.DestroyImmediate(secondObject);
            }
        }

        static GameObject CreateInactiveVolume(string objectName, out WaterVolume volume)
        {
            var gameObject = new GameObject(objectName);
            gameObject.SetActive(false);
            volume = gameObject.AddComponent<WaterVolume>();
            return gameObject;
        }
    }
}
