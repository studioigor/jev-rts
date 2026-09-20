using System;
using Jev.Gameplay.Presentation;
using Jev.Gameplay.Simulation;
using NUnit.Framework;
using UnityEngine;

namespace Jev.Gameplay.Presentation.Tests
{
    public sealed class FogOfWarVisibilityTests
    {
        [Test]
        public void CurrentExploredAndUnknownHaveDistinctMaskValues()
        {
            var pixels = new Color32[12];
            FogOfWarView.EncodeVisibility(4, 3, new[] { new Cell(1, 2) }, new[] { new Cell(2, 0) }, pixels);
            Assert.That(pixels[9].r, Is.EqualTo(255)); Assert.That(pixels[9].g, Is.EqualTo(255));
            Assert.That(pixels[2].r, Is.Zero); Assert.That(pixels[2].g, Is.EqualTo(255));
            Assert.That(pixels[0].r, Is.Zero); Assert.That(pixels[0].g, Is.Zero);
        }

        [Test]
        public void LostSightBecomesExploredAndNewMatchDoesNotInheritOldMask()
        {
            var pixels = new Color32[4]; var visited = new[] { new Cell(1, 0) };
            FogOfWarView.EncodeVisibility(2, 2, visited, visited, pixels);
            FogOfWarView.EncodeVisibility(2, 2, Array.Empty<Cell>(), visited, pixels);
            Assert.That(pixels[1].r, Is.Zero); Assert.That(pixels[1].g, Is.EqualTo(255));
            FogOfWarView.EncodeVisibility(2, 2, Array.Empty<Cell>(), Array.Empty<Cell>(), pixels);
            Assert.That(pixels[1].g, Is.Zero);
        }

        [Test]
        public void OutOfBoundsCellsCannotWrapIntoOppositeMapEdge()
        {
            var pixels = new Color32[6];
            var outside = new[] { new Cell(-1, 1), new Cell(3, 0), new Cell(0, 2), new Cell(2, -1) };
            FogOfWarView.EncodeVisibility(3, 2, outside, outside, pixels);
            foreach (var pixel in pixels) { Assert.That(pixel.r, Is.Zero); Assert.That(pixel.g, Is.Zero); }
        }
    }
}
