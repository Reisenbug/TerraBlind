using Terraria;
using Terraria.ModLoader;

namespace TerraBlind
{
    public enum SkillState { Idle, PillarBuild, PillarWait, PillarLaunch, CaveWalkBack, CavePlace, CaveJump }

    public class SkillExecutor : ModSystem
    {
        public static volatile SkillState State = SkillState.Idle;
        public static volatile bool DirectionRight = true;

        private static int _jumpFramesLeft;
        private static int _cycleTick;
        private static int _cyclesDone;
        private static int _phaseTick;
        private static bool _placeStarted;
        private static int _targetRiseTiles;

        private static int _walkBackFrames;
        private static int _currentPlaceDy;
        private static int _placeDx;

        private const int JumpHoldFrames = 15;
        private const int RestFrames = 10;
        private const int WaitFrames = 10;
        private const int LaunchFrames = 20;

        public static bool IsActive => State != SkillState.Idle;

        public static void StartPillarJump(bool dirRight, int riseTiles)
        {
            DirectionRight = dirRight;
            _targetRiseTiles = riseTiles;
            _jumpFramesLeft = 0;
            _cycleTick = 0;
            _cyclesDone = 0;
            _phaseTick = 0;
            _placeStarted = false;
            State = SkillState.PillarBuild;
        }

        public static void StartCaveBypass(bool caveOnLeft, int walkBack, int riseTiles)
        {
            DirectionRight = !caveOnLeft;
            _walkBackFrames = walkBack * 10;
            _targetRiseTiles = riseTiles;
            _placeDx = caveOnLeft ? -2 : 1;
            _currentPlaceDy = -1;
            _jumpFramesLeft = 0;
            _cyclesDone = 0;
            _phaseTick = 0;
            _placeStarted = false;
            State = SkillState.CaveWalkBack;
        }

        public static void Stop()
        {
            State = SkillState.Idle;
            PlaceCoordinator.Stop();
        }

        private static int FindPlatformSlot(Player p)
        {
            for (int i = 0; i < 10; i++)
            {
                var item = p.inventory[i];
                if (item != null && !item.IsAir && item.createTile >= 0)
                {
                    var td = Terraria.ID.TileID.Sets.Platforms;
                    if (td != null && item.createTile < td.Length && td[item.createTile])
                        return i;
                }
            }
            return -1;
        }

        public static void ApplyControls()
        {
            if (State == SkillState.Idle) return;
            var p = Main.LocalPlayer;
            if (p == null || !p.active) { Stop(); return; }

            int platformSlot = FindPlatformSlot(p);

            if (State == SkillState.PillarBuild)
            {
                if (platformSlot < 0) { Stop(); return; }
                if (!_placeStarted)
                {
                    PlaceCoordinator.Start(new PlaceRequest { Dx = 0, Dy = 0, Slot = platformSlot, RemainingFrames = 600 });
                    _placeStarted = true;
                }
                int cycleLen = JumpHoldFrames + RestFrames;
                if ((_cycleTick % cycleLen) < JumpHoldFrames)
                {
                    if (_jumpFramesLeft == 0) _jumpFramesLeft = JumpHoldFrames;
                }
                if (_jumpFramesLeft > 0) { p.controlJump = true; _jumpFramesLeft--; if (_jumpFramesLeft == 0) _jumpFramesLeft = -1; }
                else if (_jumpFramesLeft == -1) { _jumpFramesLeft = 0; }
                _cycleTick++;
                if (_cycleTick >= cycleLen) { _cycleTick = 0; _cyclesDone++; }
                if (_cyclesDone >= _targetRiseTiles)
                {
                    PlaceCoordinator.Stop();
                    _phaseTick = 0;
                    _jumpFramesLeft = 0;
                    State = SkillState.PillarWait;
                }
                return;
            }

            if (State == SkillState.PillarWait)
            {
                _phaseTick++;
                if (_phaseTick >= WaitFrames) { _phaseTick = 0; State = SkillState.PillarLaunch; }
                return;
            }

            if (State == SkillState.PillarLaunch)
            {
                if (_jumpFramesLeft == 0) _jumpFramesLeft = JumpHoldFrames;
                if (_jumpFramesLeft > 0) { p.controlJump = true; _jumpFramesLeft--; if (_jumpFramesLeft == 0) _jumpFramesLeft = -1; }
                else if (_jumpFramesLeft == -1) { _jumpFramesLeft = 0; }
                if (DirectionRight) p.controlRight = true;
                else p.controlLeft = true;
                _phaseTick++;
                if (_phaseTick >= LaunchFrames) Stop();
                return;
            }

            if (State == SkillState.CaveWalkBack)
            {
                if (!DirectionRight) p.controlRight = true;
                else p.controlLeft = true;

                int headY = (int)(p.position.Y / 16f);
                int pcx = (int)((p.position.X + p.width / 2f) / 16f);
                bool overhead = false;
                for (int dy = 1; dy <= 15; dy++)
                {
                    var t = Terraria.Main.tile[pcx, headY - dy];
                    if (t != null && t.HasTile && Main.tileSolid[t.TileType] && t.TileType != 189 && t.TileType != 196)
                    {
                        overhead = true;
                        break;
                    }
                }
                if (!overhead)
                {
                    _phaseTick++;
                    if (_phaseTick >= 10)
                    {
                        _phaseTick = 0;
                        _currentPlaceDy = -1;
                        _cyclesDone = 0;
                        State = SkillState.CavePlace;
                    }
                }
                else
                {
                    _phaseTick = 0;
                }
                return;
            }

            if (State == SkillState.CavePlace)
            {
                if (platformSlot < 0) { Stop(); return; }
                if (p.controlUseTile || Main.SmartCursorWanted)
                    p.controlUseTile = false;

                int feetY = (int)System.Math.Ceiling((p.position.Y + p.height) / 16f);
                int pcx2 = (int)System.Math.Round((p.position.X + p.width / 2f) / 16f);
                int tileX = pcx2 + _placeDx;
                int tileY = feetY + _currentPlaceDy;
                var tile = Main.tile[tileX, tileY];
                bool alreadyPlaced = tile != null && tile.HasTile;

                if (alreadyPlaced)
                {
                    _phaseTick = 30;
                }
                else
                {
                    _phaseTick++;
                    if (_phaseTick == 1)
                    {
                        PlaceCoordinator.Start(new PlaceRequest { Dx = _placeDx, Dy = _currentPlaceDy, Slot = platformSlot, RemainingFrames = 8 });
                    }
                }

                bool placed = Main.tile[tileX, tileY] != null && Main.tile[tileX, tileY].HasTile;
                if (_phaseTick >= 30 || placed)
                {
                    _phaseTick = 0;
                    _cyclesDone++;
                    _currentPlaceDy--;
                    if (_cyclesDone >= _targetRiseTiles)
                    {
                        _jumpFramesLeft = 0;
                        State = SkillState.CaveJump;
                    }
                }
                return;
            }

            if (State == SkillState.CaveJump)
            {
                if (_jumpFramesLeft == 0) _jumpFramesLeft = JumpHoldFrames;
                if (_jumpFramesLeft > 0) { p.controlJump = true; _jumpFramesLeft--; if (_jumpFramesLeft == 0) _jumpFramesLeft = -1; }
                else if (_jumpFramesLeft == -1) { _jumpFramesLeft = 0; }
                if (!DirectionRight) p.controlLeft = true;
                else p.controlRight = true;
                _phaseTick++;
                if (_phaseTick >= LaunchFrames) Stop();
            }
        }
    }
}
