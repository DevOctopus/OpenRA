#region Copyright & License Information
/*
 * Copyright 2007-2015 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation. For more information,
 * see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	public class ShroudRendererInfo : ITraitInfo
	{
		public readonly string Sequence = "shroud";
		public readonly string[] ShroudVariants = new[] { "shroud" };
		public readonly string[] FogVariants = new[] { "fog" };

		public readonly string ShroudPalette = "shroud";
		public readonly string FogPalette = "fog";

		[Desc("Bitfield of shroud directions for each frame. Lower four bits are",
			"corners clockwise from TL; upper four are edges clockwise from top")]
		public readonly int[] Index = new[] { 12, 9, 8, 3, 1, 6, 4, 2, 13, 11, 7, 14 };

		[Desc("Use the upper four bits when calculating frame")]
		public readonly bool UseExtendedIndex = false;

		[Desc("Override for source art that doesn't define a fully shrouded tile")]
		public readonly string OverrideFullShroud = null;
		public readonly int OverrideShroudIndex = 15;

		[Desc("Override for source art that doesn't define a fully fogged tile")]
		public readonly string OverrideFullFog = null;
		public readonly int OverrideFogIndex = 15;

		public readonly BlendMode ShroudBlend = BlendMode.Alpha;
		public object Create(ActorInitializer init) { return new ShroudRenderer(init.World, this); }
	}

	public sealed class ShroudRenderer : IRenderShroud, IWorldLoaded, INotifyActorDisposing
	{
		[Flags]
		enum Edges : byte
		{
			None = 0,
			TopLeft = 0x01,
			TopRight = 0x02,
			BottomRight = 0x04,
			BottomLeft = 0x08,
			AllCorners = TopLeft | TopRight | BottomRight | BottomLeft,
			TopSide = 0x10,
			RightSide = 0x20,
			BottomSide = 0x40,
			LeftSide = 0x80,
			AllSides = TopSide | RightSide | BottomSide | LeftSide,
			Top = TopSide | TopLeft | TopRight,
			Right = RightSide | TopRight | BottomRight,
			Bottom = BottomSide | BottomRight | BottomLeft,
			Left = LeftSide | TopLeft | BottomLeft,
			All = Top | Right | Bottom | Left
		}

		struct TileInfo
		{
			public readonly float2 ScreenPosition;
			public readonly byte Variant;

			public TileInfo(float2 screenPosition, byte variant)
			{
				ScreenPosition = screenPosition;
				Variant = variant;
			}
		}

		readonly ShroudRendererInfo info;
		readonly Map map;
		readonly Edges notVisibleEdges;
		readonly byte variantStride;
		readonly byte[] edgesToSpriteIndexOffset;

		readonly CellLayer<TileInfo> tileInfos;
		readonly Sprite[] fogSprites, shroudSprites;
		readonly HashSet<CPos> cellsDirty = new HashSet<CPos>();
		readonly HashSet<CPos> cellsAndNeighborsDirty = new HashSet<CPos>();

		Shroud currentShroud;
		Func<MPos, bool> visibleUnderShroud, visibleUnderFog;
		TerrainSpriteLayer shroudLayer, fogLayer;

		public ShroudRenderer(World world, ShroudRendererInfo info)
		{
			if (info.ShroudVariants.Length != info.FogVariants.Length)
				throw new ArgumentException("ShroudRenderer must define the same number of shroud and fog variants!", "info");

			if ((info.OverrideFullFog == null) ^ (info.OverrideFullShroud == null))
				throw new ArgumentException("ShroudRenderer cannot define overrides for only one of shroud or fog!", "info");

			if (info.ShroudVariants.Length > byte.MaxValue)
				throw new ArgumentException("ShroudRenderer cannot define this many shroud and fog variants.", "info");

			if (info.Index.Length >= byte.MaxValue)
				throw new ArgumentException("ShroudRenderer cannot define this many indexes for shroud directions.", "info");

			this.info = info;
			map = world.Map;

			tileInfos = new CellLayer<TileInfo>(map);

			// Load sprite variants
			var variantCount = info.ShroudVariants.Length;
			variantStride = (byte)(info.Index.Length + (info.OverrideFullShroud != null ? 1 : 0));
			shroudSprites = new Sprite[variantCount * variantStride];
			fogSprites = new Sprite[variantCount * variantStride];

			for (var j = 0; j < variantCount; j++)
			{
				var shroud = map.SequenceProvider.GetSequence(info.Sequence, info.ShroudVariants[j]);
				var fog = map.SequenceProvider.GetSequence(info.Sequence, info.FogVariants[j]);
				for (var i = 0; i < info.Index.Length; i++)
				{
					shroudSprites[j * variantStride + i] = shroud.GetSprite(i);
					fogSprites[j * variantStride + i] = fog.GetSprite(i);
				}

				if (info.OverrideFullShroud != null)
				{
					var i = (j + 1) * variantStride - 1;
					shroudSprites[i] = map.SequenceProvider.GetSequence(info.Sequence, info.OverrideFullShroud).GetSprite(0);
					fogSprites[i] = map.SequenceProvider.GetSequence(info.Sequence, info.OverrideFullFog).GetSprite(0);
				}
			}

			// Mapping of shrouded directions -> sprite index
			edgesToSpriteIndexOffset = new byte[(byte)(info.UseExtendedIndex ? Edges.All : Edges.AllCorners) + 1];
			for (var i = 0; i < info.Index.Length; i++)
				edgesToSpriteIndexOffset[info.Index[i]] = (byte)i;

			if (info.OverrideFullShroud != null)
				edgesToSpriteIndexOffset[info.OverrideShroudIndex] = (byte)(variantStride - 1);

			notVisibleEdges = info.UseExtendedIndex ? Edges.AllSides : Edges.AllCorners;
		}

		public void WorldLoaded(World w, WorldRenderer wr)
		{
			// Initialize tile cache
			// This includes the region outside the visible area to cover any sprites peeking outside the map
			foreach (var uv in w.Map.AllCells.MapCoords)
			{
				var screen = wr.ScreenPosition(w.Map.CenterOfCell(uv.ToCPos(map)));
				var variant = (byte)Game.CosmeticRandom.Next(info.ShroudVariants.Length);
				tileInfos[uv] = new TileInfo(screen, variant);
			}

			DirtyCells(map.AllCells);

			// All tiles are visible in the editor
			if (w.Type == WorldType.Editor)
				visibleUnderShroud = _ => true;
			else
				visibleUnderShroud = map.Contains;

			visibleUnderFog = map.Contains;

			var shroudSheet = shroudSprites[0].Sheet;
			if (shroudSprites.Any(s => s.Sheet != shroudSheet))
				throw new InvalidDataException("Shroud sprites span multiple sheets. Try loading their sequences earlier.");

			var shroudBlend = shroudSprites[0].BlendMode;
			if (shroudSprites.Any(s => s.BlendMode != shroudBlend))
				throw new InvalidDataException("Shroud sprites must all use the same blend mode.");

			var fogSheet = fogSprites[0].Sheet;
			if (fogSprites.Any(s => s.Sheet != fogSheet))
				throw new InvalidDataException("Fog sprites span multiple sheets. Try loading their sequences earlier.");

			var fogBlend = fogSprites[0].BlendMode;
			if (fogSprites.Any(s => s.BlendMode != fogBlend))
				throw new InvalidDataException("Fog sprites must all use the same blend mode.");

			shroudLayer = new TerrainSpriteLayer(w, wr, shroudSheet, shroudBlend, wr.Palette(info.ShroudPalette), false);
			fogLayer = new TerrainSpriteLayer(w, wr, fogSheet, fogBlend, wr.Palette(info.FogPalette), false);
		}

		Edges GetEdges(MPos uv, Func<MPos, bool> isVisible)
		{
			if (!isVisible(uv))
				return notVisibleEdges;

			var cell = uv.ToCPos(map);

			// If a side is shrouded then we also count the corners.
			var edge = Edges.None;
			if (!isVisible((cell + new CVec(0, -1)).ToMPos(map))) edge |= Edges.Top;
			if (!isVisible((cell + new CVec(1, 0)).ToMPos(map))) edge |= Edges.Right;
			if (!isVisible((cell + new CVec(0, 1)).ToMPos(map))) edge |= Edges.Bottom;
			if (!isVisible((cell + new CVec(-1, 0)).ToMPos(map))) edge |= Edges.Left;

			var ucorner = edge & Edges.AllCorners;
			if (!isVisible((cell + new CVec(-1, -1)).ToMPos(map))) edge |= Edges.TopLeft;
			if (!isVisible((cell + new CVec(1, -1)).ToMPos(map))) edge |= Edges.TopRight;
			if (!isVisible((cell + new CVec(1, 1)).ToMPos(map))) edge |= Edges.BottomRight;
			if (!isVisible((cell + new CVec(-1, 1)).ToMPos(map))) edge |= Edges.BottomLeft;

			// RA provides a set of frames for tiles with shrouded
			// corners but unshrouded edges. We want to detect this
			// situation without breaking the edge -> corner enabling
			// in other combinations. The XOR turns off the corner
			// bits that are enabled twice, which gives the behavior
			// we want here.
			return info.UseExtendedIndex ? edge ^ ucorner : edge & Edges.AllCorners;
		}

		void DirtyCells(IEnumerable<CPos> cells)
		{
			cellsDirty.UnionWith(cells);
		}

		public void RenderShroud(WorldRenderer wr, Shroud shroud)
		{
			if (currentShroud != shroud)
			{
				if (currentShroud != null)
					currentShroud.CellsChanged -= DirtyCells;

				if (shroud != null)
				{
					shroud.CellsChanged += DirtyCells;

					// Needs the anonymous function to ensure the correct overload is chosen
					visibleUnderShroud = uv => currentShroud.IsExplored(uv);
					visibleUnderFog = uv => currentShroud.IsVisible(uv);
				}
				else
				{
					visibleUnderShroud = map.Contains;
					visibleUnderFog = map.Contains;
				}

				currentShroud = shroud;
				DirtyCells(map.CellsInsideBounds);
			}

			// We need to update newly dirtied areas of the shroud.
			// Expand the dirty area to cover the neighboring cells, since shroud is affected by neighboring cells.
			foreach (var cell in cellsDirty)
			{
				cellsAndNeighborsDirty.Add(cell);
				foreach (var direction in CVec.Directions)
					cellsAndNeighborsDirty.Add(cell + direction);
			}

			foreach (var cell in cellsAndNeighborsDirty)
			{
				var uv = cell.ToMPos(map.TileShape);
				if (!tileInfos.Contains(uv))
					continue;

				var tileInfo = tileInfos[uv];
				var shroudSprite = GetSprite(shroudSprites, GetEdges(uv, visibleUnderShroud), tileInfo.Variant);
				var shroudPos = tileInfo.ScreenPosition;
				if (shroudSprite != null)
					shroudPos += shroudSprite.Offset - 0.5f * shroudSprite.Size;

				var fogSprite = GetSprite(fogSprites, GetEdges(uv, visibleUnderFog), tileInfo.Variant);
				var fogPos = tileInfo.ScreenPosition;
				if (fogSprite != null)
					fogPos += fogSprite.Offset - 0.5f * fogSprite.Size;

				shroudLayer.Update(uv, shroudSprite, shroudPos);
				fogLayer.Update(uv, fogSprite, fogPos);
			}

			cellsDirty.Clear();
			cellsAndNeighborsDirty.Clear();

			fogLayer.Draw(wr.Viewport);
			shroudLayer.Draw(wr.Viewport);
		}

		Sprite GetSprite(Sprite[] sprites, Edges edges, int variant)
		{
			if (edges == Edges.None)
				return null;

			return sprites[variant * variantStride + edgesToSpriteIndexOffset[(byte)edges]];
		}

		bool disposed;
		public void Disposing(Actor self)
		{
			if (disposed)
				return;

			shroudLayer.Dispose();
			fogLayer.Dispose();
			disposed = true;
		}
	}
}
