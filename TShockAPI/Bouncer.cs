/*
TShock, a server mod for Terraria
Copyright (C) 2011-2017 Nyx Studios (fka. The TShock Team)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Streams;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Terraria.ID;
using TShockAPI.DB;
using TShockAPI.Net;
using Terraria;
using Terraria.ObjectData;
using Terraria.DataStructures;
using Terraria.GameContent.Tile_Entities;
using Terraria.Localization;
using Microsoft.Xna.Framework;
using OTAPI.Tile;
using TShockAPI.Localization;
using static TShockAPI.GetDataHandlers;
using TerrariaApi.Server;


namespace TShockAPI
{
	/// <summary>Bouncer is the TShock anti-hack and build guardian system</summary>
	internal sealed class Bouncer
	{
		/// <summary>Constructor call initializes Bouncer & related functionality.</summary>
		/// <returns>A new Bouncer.</returns>
		internal Bouncer(TerrariaPlugin pluginInstance)
		{
			// Setup hooks

			GetDataHandlers.SendTileSquare.Register(OnSendTileSquare);
			GetDataHandlers.HealOtherPlayer.Register(OnHealOtherPlayer);
			GetDataHandlers.TileEdit.Register(OnTileEdit);
		}

		internal void OnTileEdit(object sender, GetDataHandlers.TileEditEventArgs args)
		{
			EditAction action = args.Action;
			int tileX = args.X;
			int tileY = args.Y;
			short editData = args.EditData;
			EditType type = args.editDetail;
			byte style = args.Style;

			try
			{
				if (editData < 0)
				{
					args.Player.SendTileSquare(tileX, tileY, 4);
					args.Handled = true;
					return;
				}

				if (!TShock.Utils.TilePlacementValid(tileX, tileY))
					args.Handled = true;
					return;
				if (action == EditAction.KillTile && Main.tile[tileX, tileY].type == TileID.MagicalIceBlock)
					args.Handled = false;
					return;
				if (args.Player.Dead && TShock.Config.PreventDeadModification)
					args.Handled = true;
					return;

				if (args.Player.AwaitingName)
				{
					bool includeUnprotected = false;
					bool includeZIndexes = false;
					bool persistentMode = false;
					foreach (string parameter in args.Player.AwaitingNameParameters)
					{
						if (parameter.Equals("-u", StringComparison.InvariantCultureIgnoreCase))
							includeUnprotected = true;
						if (parameter.Equals("-z", StringComparison.InvariantCultureIgnoreCase))
							includeZIndexes = true;
						if (parameter.Equals("-p", StringComparison.InvariantCultureIgnoreCase))
							persistentMode = true;
					}

					List<string> outputRegions = new List<string>();
					foreach (Region region in TShock.Regions.Regions.OrderBy(r => r.Z).Reverse())
					{
						if (!includeUnprotected && !region.DisableBuild)
							continue;
						if (tileX < region.Area.Left || tileX > region.Area.Right)
							continue;
						if (tileY < region.Area.Top || tileY > region.Area.Bottom)
							continue;

						string format = "{1}";
						if (includeZIndexes)
							format = "{1} (z:{0})";

						outputRegions.Add(string.Format(format, region.Z, region.Name));
					}

					if (outputRegions.Count == 0)
					{
						if (includeUnprotected)
							args.Player.SendInfoMessage("There are no regions at this point.");
						else
							args.Player.SendInfoMessage("There are no regions at this point or they are not protected.");
					}
					else
					{
						if (includeUnprotected)
							args.Player.SendSuccessMessage("Regions at this point:");
						else
							args.Player.SendSuccessMessage("Protected regions at this point:");

						foreach (string line in PaginationTools.BuildLinesFromTerms(outputRegions))
							args.Player.SendMessage(line, Color.White);
					}

					if (!persistentMode)
					{
						args.Player.AwaitingName = false;
						args.Player.AwaitingNameParameters = null;
					}

					args.Player.SendTileSquare(tileX, tileY, 4);
					args.Handled = true;
					return;
				}

				if (args.Player.AwaitingTempPoint > 0)
				{
					args.Player.TempPoints[args.Player.AwaitingTempPoint - 1].X = tileX;
					args.Player.TempPoints[args.Player.AwaitingTempPoint - 1].Y = tileY;
					args.Player.SendInfoMessage("Set temp point {0}.", args.Player.AwaitingTempPoint);
					args.Player.SendTileSquare(tileX, tileY, 4);
					args.Player.AwaitingTempPoint = 0;
					args.Handled = true;
					return;
				}

				Item selectedItem = args.Player.SelectedItem;
				int lastKilledProj = args.Player.LastKilledProjectile;
				ITile tile = Main.tile[tileX, tileY];

				if (action == EditAction.PlaceTile)
				{
					if (TShock.TileBans.TileIsBanned(editData, args.Player))
					{
						args.Player.SendTileSquare(tileX, tileY, 1);
						args.Player.SendErrorMessage("You do not have permission to place this tile.");
						args.Handled = true;
						return;
					}
				}

				if (action == EditAction.KillTile && !Main.tileCut[tile.type] && !breakableTiles.Contains(tile.type))
				{
					//TPlayer.mount.Type 8 => Drill Containment Unit.

					// If the tile is an axe tile and they aren't selecting an axe, they're hacking.
					if (Main.tileAxe[tile.type] && ((args.Player.TPlayer.mount.Type != 8 && selectedItem.axe == 0) && !ItemID.Sets.Explosives[selectedItem.netID] && args.Player.RecentFuse == 0))
					{
						args.Player.SendTileSquare(tileX, tileY, 4);
						args.Handled = true;
						return;
					}
					// If the tile is a hammer tile and they aren't selecting a hammer, they're hacking.
					else if (Main.tileHammer[tile.type] && ((args.Player.TPlayer.mount.Type != 8 && selectedItem.hammer == 0) && !ItemID.Sets.Explosives[selectedItem.netID] && args.Player.RecentFuse == 0))
					{
						args.Player.SendTileSquare(tileX, tileY, 4);
						args.Handled = true;
						return;
					}
					// If the tile is a pickaxe tile and they aren't selecting a pickaxe, they're hacking.
					// Item frames can be modified without pickaxe tile.
					else if (tile.type != TileID.ItemFrame
						&& !Main.tileAxe[tile.type] && !Main.tileHammer[tile.type] && tile.wall == 0 && args.Player.TPlayer.mount.Type != 8 && selectedItem.pick == 0 && !ItemID.Sets.Explosives[selectedItem.netID] && args.Player.RecentFuse == 0)
					{
						args.Player.SendTileSquare(tileX, tileY, 4);
						args.Handled = true;
						return;
					}
				}
				else if (action == EditAction.KillWall)
				{
					// If they aren't selecting a hammer, they could be hacking.
					if (selectedItem.hammer == 0 && !ItemID.Sets.Explosives[selectedItem.netID] && args.Player.RecentFuse == 0 && selectedItem.createWall == 0)
					{
						args.Player.SendTileSquare(tileX, tileY, 1);
						args.Handled = true;
						return;
					}
				}
				else if (action == EditAction.PlaceTile && (projectileCreatesTile.ContainsKey(lastKilledProj) && editData == projectileCreatesTile[lastKilledProj]))
				{
					args.Player.LastKilledProjectile = 0;
				}
				else if (action == EditAction.PlaceTile || action == EditAction.PlaceWall)
				{
					if ((action == EditAction.PlaceTile && TShock.Config.PreventInvalidPlaceStyle) &&
						(MaxPlaceStyles.ContainsKey(editData) && style > MaxPlaceStyles[editData]) &&
						(ExtraneousPlaceStyles.ContainsKey(editData) && style > ExtraneousPlaceStyles[editData]))
					{
						args.Player.SendTileSquare(tileX, tileY, 4);
						args.Handled = true;
						return;
					}

					// If they aren't selecting the item which creates the tile or wall, they're hacking.
					if (!(selectedItem.netID == ItemID.IceRod && editData == TileID.MagicalIceBlock) &&
						(editData != (action == EditAction.PlaceTile ? selectedItem.createTile : selectedItem.createWall) &&
						!(ropeCoilPlacements.ContainsKey(selectedItem.netID) && editData == ropeCoilPlacements[selectedItem.netID])))
					{
						args.Player.SendTileSquare(tileX, tileY, 4);
						args.Handled = true;
						return;
					}

					// Using the actuation accessory can lead to actuator hacking
					if (TShock.Itembans.ItemIsBanned("Actuator", args.Player) && args.Player.TPlayer.autoActuator)
					{
						args.Player.SendTileSquare(tileX, tileY, 1);
						args.Player.SendErrorMessage("You do not have permission to place actuators.");
						args.Handled = true;
						return;
					}
					if (TShock.Itembans.ItemIsBanned(EnglishLanguage.GetItemNameById(selectedItem.netID), args.Player) || editData >= (action == EditAction.PlaceTile ? Main.maxTileSets : Main.maxWallTypes))
					{
						args.Player.SendTileSquare(tileX, tileY, 4);
						args.Handled = true;
						return;
					}
					if (action == EditAction.PlaceTile && (editData == 29 || editData == 97) && Main.ServerSideCharacter)
					{
						args.Player.SendErrorMessage("You cannot place this tile because server side characters are enabled.");
						args.Player.SendTileSquare(tileX, tileY, 3);
						args.Handled = true;
						return;
					}
					if (action == EditAction.PlaceTile && (editData == TileID.Containers || editData == TileID.Containers2))
					{
						if (TShock.Utils.MaxChests())
						{
							args.Player.SendErrorMessage("The world's chest limit has been reached - unable to place more.");
							args.Player.SendTileSquare(tileX, tileY, 3);
							args.Handled = true;
							return;
						}
						if ((TShock.Utils.TilePlacementValid(tileX, tileY + 1) && Main.tile[tileX, tileY + 1].type == TileID.Boulder) ||
							(TShock.Utils.TilePlacementValid(tileX + 1, tileY + 1) && Main.tile[tileX + 1, tileY + 1].type == TileID.Boulder))
						{
							args.Player.SendTileSquare(tileX, tileY, 3);
							args.Handled = true;
							return;
						}
					}
				}
				else if (action == EditAction.PlaceWire || action == EditAction.PlaceWire2 || action == EditAction.PlaceWire3)
				{
					// If they aren't selecting a wrench, they're hacking.
					// WireKite = The Grand Design
					if (selectedItem.type != ItemID.Wrench
						&& selectedItem.type != ItemID.BlueWrench
						&& selectedItem.type != ItemID.GreenWrench
						&& selectedItem.type != ItemID.YellowWrench
						&& selectedItem.type != ItemID.MulticolorWrench
						&& selectedItem.type != ItemID.WireKite)
					{
						args.Player.SendTileSquare(tileX, tileY, 1);
						args.Handled = true;
						return;
					}
				}
				else if (action == EditAction.KillActuator || action == EditAction.KillWire ||
					action == EditAction.KillWire2 || action == EditAction.KillWire3)
				{
					// If they aren't selecting the wire cutter, they're hacking.
					if (selectedItem.type != ItemID.WireCutter
						&& selectedItem.type != ItemID.WireKite
						&& selectedItem.type != ItemID.MulticolorWrench)
					{
						args.Player.SendTileSquare(tileX, tileY, 1);
						args.Handled = true;
						return;
					}
				}
				else if (action == EditAction.PlaceActuator)
				{
					// If they aren't selecting the actuator and don't have the Presserator equipped, they're hacking.
					if (selectedItem.type != ItemID.Actuator && !args.Player.TPlayer.autoActuator)
					{
						args.Player.SendTileSquare(tileX, tileY, 1);
						args.Handled = true;
						return;
					}
				}
				if (TShock.Config.AllowCutTilesAndBreakables && Main.tileCut[tile.type])
				{
					if (action == EditAction.KillWall)
					{
						args.Player.SendTileSquare(tileX, tileY, 1);
						args.Handled = true;
						return;
					}
					args.Handled = false;
					return;
				}

				if (TShock.CheckIgnores(args.Player))
				{
					args.Player.SendTileSquare(tileX, tileY, 4);
					args.Handled = true;
					return;
				}

				if (TShock.CheckTilePermission(args.Player, tileX, tileY, editData, action))
				{
					args.Player.SendTileSquare(tileX, tileY, 4);
					args.Handled = true;
					return;
				}

				if (TShock.CheckRangePermission(args.Player, tileX, tileY))
				{
					if (action == EditAction.PlaceTile && (editData == TileID.Rope || editData == TileID.SilkRope || editData == TileID.VineRope || editData == TileID.WebRope))
					{
						args.Handled = false;
						return;
					}

					if (action == EditAction.KillTile || action == EditAction.KillWall && ItemID.Sets.Explosives[selectedItem.netID] && args.Player.RecentFuse == 0)
					{
						args.Handled = false;
						return;
					}

					args.Player.SendTileSquare(tileX, tileY, 4);
					args.Handled = true;
					return;
				}

				if (args.Player.TileKillThreshold >= TShock.Config.TileKillThreshold)
				{
					args.Player.Disable("Reached TileKill threshold.", DisableFlags.WriteToLogAndConsole);
					args.Player.SendTileSquare(tileX, tileY, 4);
					args.Handled = true;
					return;
				}

				if (args.Player.TilePlaceThreshold >= TShock.Config.TilePlaceThreshold)
				{
					args.Player.Disable("Reached TilePlace threshold.", DisableFlags.WriteToLogAndConsole);
					args.Player.SendTileSquare(tileX, tileY, 4);
					args.Handled = true;
					return;
				}

				if ((DateTime.UtcNow - args.Player.LastThreat).TotalMilliseconds < 5000)
				{
					args.Player.SendTileSquare(tileX, tileY, 4);
					args.Handled = true;
					return;
				}

				if ((action == EditAction.PlaceTile || action == EditAction.PlaceWall) && !args.Player.HasPermission(Permissions.ignoreplacetiledetection))
				{
					args.Player.TilePlaceThreshold++;
					var coords = new Vector2(tileX, tileY);
					lock (args.Player.TilesCreated)
						if (!args.Player.TilesCreated.ContainsKey(coords))
							args.Player.TilesCreated.Add(coords, Main.tile[tileX, tileY]);
				}

				if ((action == EditAction.KillTile || action == EditAction.KillTileNoItem || action == EditAction.KillWall) && Main.tileSolid[Main.tile[tileX, tileY].type] &&
					!args.Player.HasPermission(Permissions.ignorekilltiledetection))
				{
					args.Player.TileKillThreshold++;
					var coords = new Vector2(tileX, tileY);
					lock (args.Player.TilesDestroyed)
						if (!args.Player.TilesDestroyed.ContainsKey(coords))
							args.Player.TilesDestroyed.Add(coords, Main.tile[tileX, tileY]);
				}
				args.Handled = false;
				return;
			}
			catch
			{
				args.Player.SendTileSquare(tileX, tileY, 4);
				args.Handled = true;
				return;
			}
		}

		/// <summary>The handler for the HealOther events in Bouncer</summary>
		/// <param name="sender">sender</param>
		/// <param name="args">args</param>
		internal void OnHealOtherPlayer(object sender, GetDataHandlers.HealOtherPlayerEventArgs args)
		{
			short amount = args.Amount;
			byte plr = args.TargetPlayerIndex;

			if (amount <= 0 || Main.player[plr] == null || !Main.player[plr].active)
			{
				args.Handled = true;
				return;
			}

			if (amount > TShock.Config.MaxDamage * 0.2)
			{
				args.Player.Disable("HealOtherPlayer cheat attempt!", DisableFlags.WriteToLogAndConsole);
				args.Handled = true;
				return;
			}

			if (args.Player.HealOtherThreshold > TShock.Config.HealOtherThreshold)
			{
				args.Player.Disable("Reached HealOtherPlayer threshold.", DisableFlags.WriteToLogAndConsole);
				args.Handled = true;
				return;
			}

			if (TShock.CheckIgnores(args.Player) || (DateTime.UtcNow - args.Player.LastThreat).TotalMilliseconds < 5000)
			{
				args.Handled = true;
				return;
			}

			args.Player.HealOtherThreshold++;
			args.Handled = false;
			return;
		}

		/// <summary>The handler for SendTileSquare events in Bouncer</summary>
		/// <param name="sender">sender</param>
		/// <param name="args">args</param>
		internal void OnSendTileSquare(object sender, GetDataHandlers.SendTileSquareEventArgs args)
		{
			short size = args.Size;
			int tileX = args.TileX;
			int tileY = args.TileY;

			if (args.Player.HasPermission(Permissions.allowclientsideworldedit))
			{
				args.Handled = false;
				return;
			}

			// From White:
			// IIRC it's because 5 means a 5x5 square which is normal for a tile square, and anything bigger is a non-vanilla tile modification attempt
			if (size > 5)
			{
				args.Handled = true;
				return;
			}

			if ((DateTime.UtcNow - args.Player.LastThreat).TotalMilliseconds < 5000)
			{
				args.Player.SendTileSquare(tileX, tileY, size);
				args.Handled = true;
				return;
			}

			if (TShock.CheckIgnores(args.Player))
			{
				args.Player.SendTileSquare(tileX, tileY, size);
				args.Handled = true;
				return;
			}

			try
			{
				var tiles = new NetTile[size, size];
				for (int x = 0; x < size; x++)
				{
					for (int y = 0; y < size; y++)
					{
						tiles[x, y] = new NetTile(args.Data);
					}
				}

				bool changed = false;
				for (int x = 0; x < size; x++)
				{
					int realx = tileX + x;
					if (realx < 0 || realx >= Main.maxTilesX)
						continue;

					for (int y = 0; y < size; y++)
					{
						int realy = tileY + y;
						if (realy < 0 || realy >= Main.maxTilesY)
							continue;

						var tile = Main.tile[realx, realy];
						var newtile = tiles[x, y];
						if (TShock.CheckTilePermission(args.Player, realx, realy) ||
							TShock.CheckRangePermission(args.Player, realx, realy))
						{
							continue;
						}

						// Fixes the Flower Boots not creating flowers issue
						if (size == 1 && args.Player.Accessories.Any(i => i.active && i.netID == ItemID.FlowerBoots))
						{
							if (Main.tile[realx, realy + 1].type == TileID.Grass && (newtile.Type == TileID.Plants || newtile.Type == TileID.Plants2))
							{
								args.Handled = false;
								return;
							}

							if (Main.tile[realx, realy + 1].type == TileID.HallowedGrass && (newtile.Type == TileID.HallowedPlants || newtile.Type == TileID.HallowedPlants2))
							{
								args.Handled = false;
								return;
							}

							if (Main.tile[realx, realy + 1].type == TileID.JungleGrass && newtile.Type == TileID.JunglePlants2)
							{
								args.Handled = false;
								return;
							}
						}

						// Junction Box
						if (tile.type == TileID.WirePipe)
						{
							args.Handled = false;
							return;
						}

						// Orientable tiles
						if (tile.type == newtile.Type && orientableTiles.Contains(tile.type))
						{
							Main.tile[realx, realy].frameX = newtile.FrameX;
							Main.tile[realx, realy].frameY = newtile.FrameY;
							changed = true;
						}
						// Landmine
						if (tile.type == TileID.LandMine && !newtile.Active)
						{
							Main.tile[realx, realy].active(false);
							changed = true;
						}
						// Sensors
						if(newtile.Type == TileID.LogicSensor && !Main.tile[realx, realy].active())
						{
							Main.tile[realx, realy].type = newtile.Type;
							Main.tile[realx, realy].frameX = newtile.FrameX;
							Main.tile[realx, realy].frameY = newtile.FrameY;
							Main.tile[realx, realy].active(true);
							changed = true;
						}

						if (tile.active() && newtile.Active && tile.type != newtile.Type)
						{
							// Grass <-> Grass
							if ((TileID.Sets.Conversion.Grass[tile.type] && TileID.Sets.Conversion.Grass[newtile.Type]) ||
								// Dirt <-> Dirt
								((tile.type == 0 || tile.type == 59) &&
								(newtile.Type == 0 || newtile.Type == 59)) ||
								// Ice <-> Ice
								(TileID.Sets.Conversion.Ice[tile.type] && TileID.Sets.Conversion.Ice[newtile.Type]) ||
								// Stone <-> Stone
								((TileID.Sets.Conversion.Stone[tile.type] || Main.tileMoss[tile.type]) &&
								(TileID.Sets.Conversion.Stone[newtile.Type] || Main.tileMoss[newtile.Type])) ||
								// Sand <-> Sand
								(TileID.Sets.Conversion.Sand[tile.type] && TileID.Sets.Conversion.Sand[newtile.Type]) ||
								// Sandstone <-> Sandstone
								(TileID.Sets.Conversion.Sandstone[tile.type] && TileID.Sets.Conversion.Sandstone[newtile.Type]) ||
								// Hardened Sand <-> Hardened Sand
								(TileID.Sets.Conversion.HardenedSand[tile.type] && TileID.Sets.Conversion.HardenedSand[newtile.Type]))
							{
								Main.tile[realx, realy].type = newtile.Type;
								changed = true;
							}
						}
						// Stone wall <-> Stone wall
						if (((tile.wall == 1 || tile.wall == 3 || tile.wall == 28 || tile.wall == 83) &&
							(newtile.Wall == 1 || newtile.Wall == 3 || newtile.Wall == 28 || newtile.Wall == 83)) ||
							// Leaf wall <-> Leaf wall
							(((tile.wall >= 63 && tile.wall <= 70) || tile.wall == 81) &&
							((newtile.Wall >= 63 && newtile.Wall <= 70) || newtile.Wall == 81)))
						{
							Main.tile[realx, realy].wall = newtile.Wall;
							changed = true;
						}

						if ((tile.type == TileID.TrapdoorClosed && (newtile.Type == TileID.TrapdoorOpen || !newtile.Active)) ||
							(tile.type == TileID.TrapdoorOpen && (newtile.Type == TileID.TrapdoorClosed || !newtile.Active)) ||
							(!tile.active() && newtile.Active && (newtile.Type == TileID.TrapdoorOpen||newtile.Type == TileID.TrapdoorClosed)))
						{
							Main.tile[realx, realy].type = newtile.Type;
							Main.tile[realx, realy].frameX = newtile.FrameX;
							Main.tile[realx, realy].frameY = newtile.FrameY;
							Main.tile[realx, realy].active(newtile.Active);
							changed = true;
						}
					}
				}

				if (changed)
				{
					TSPlayer.All.SendTileSquare(tileX, tileY, size + 1);
					WorldGen.RangeFrame(tileX, tileY, tileX + size, tileY + size);
				}
				else
				{
					args.Player.SendTileSquare(tileX, tileY, size);
				}
			}
			catch
			{
				args.Player.SendTileSquare(tileX, tileY, size);
			}
			args.Handled = false;
			return;
		}

		/// <summary>
		/// Tile IDs that can be oriented:
		/// Cannon,
		/// Chairs,
		/// Beds,
		/// Bathtubs,
		/// Statues,
		/// Mannequin,
		/// Traps,
		/// MusicBoxes,
		/// ChristmasTree,
		/// WaterFountain,
		/// Womannequin,
		/// MinecartTrack,
		/// WeaponsRack,
		/// LunarMonolith,
		/// TargetDummy,
		/// Campfire
		/// </summary>
		private static int[] orientableTiles = new int[]
		{
			TileID.Cannon,
			TileID.Chairs,
			TileID.Beds,
			TileID.Bathtubs,
			TileID.Statues,
			TileID.Mannequin,
			TileID.Traps,
			TileID.MusicBoxes,
			TileID.ChristmasTree,
			TileID.WaterFountain,
			TileID.Womannequin,
			TileID.MinecartTrack,
			TileID.WeaponsRack,
			TileID.ItemFrame,
			TileID.LunarMonolith,
			TileID.TargetDummy,
			TileID.Campfire
		};

	}
}