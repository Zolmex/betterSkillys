﻿using Mono.Game;
using System;
using System.Collections.Generic;
using wServer.networking;
using wServer.networking.packets.outgoing;

namespace wServer.core.objects
{
    internal class Decoy : StaticObject, IPlayer
    {

        private readonly int duration;

        private Vector2 direction;
        private bool exploded = false;
        private Player player;

        public Decoy(Player player, int duration) : base(player.GameServer, 0x0715, duration, true, true, true)
        {
            this.player = player;
            this.duration = duration;

            var history = player.TryGetHistory(1);

            if (history == null)
                direction = GetRandDirection();
            else
            {
                direction = new Vector2(player.X - history.Value.X, player.Y - history.Value.Y);

                if (direction.LengthSquared() == 0)
                    direction = GetRandDirection();
                else
                    direction.Normalize();
            }
        }

        public void Damage(int dmg, Entity src)
        { }

        public bool IsVisibleToEnemy() => true;

        public override void Tick(ref TickTime time)
        {
            if (HP > duration - 2000)
                ValidateAndMove(X + direction.X * 1.0f * time.BehaviourTickTime, Y + direction.Y * 1.0f * time.BehaviourTickTime);

            if (HP < 250 && !exploded)
            {
                exploded = true;

                World.BroadcastIfVisible(new ShowEffect()
                {
                    EffectType = EffectType.AreaBlast,
                    Color = new ARGB(0xffff0000),
                    TargetObjectId = Id,
                    Pos1 = new Position() { X = 1 }
                }, this);
            }

            base.Tick(ref time);
        }

        protected override void ExportStats(IDictionary<StatDataType, object> stats)
        {
            stats[StatDataType.Texture1] = player.Texture1;
            stats[StatDataType.Texture2] = player.Texture2;

            base.ExportStats(stats);
        }

        private Vector2 GetRandDirection()
        {
            var angle = World.Random.NextDouble() * 2 * Math.PI;
            return new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
        }
    }
}
