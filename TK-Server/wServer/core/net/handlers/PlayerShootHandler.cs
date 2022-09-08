﻿using common;
using wServer.core;
using wServer.core.objects;
using wServer.networking;
using wServer.networking.packets.outgoing;

namespace wServer.core.net.handlers
{
    public class PlayerShootHandler : IMessageHandler
    {
        public override MessageId MessageId => MessageId.PLAYERSHOOT;

        public override void Handle(Client client, NReader rdr, ref TickTime tickTime)
        {
            var Time = rdr.ReadInt32();
            var BulletId = rdr.ReadByte();
            var ContainerType = rdr.ReadInt32();
            var StartingPos = Position.Read(rdr);
            var Angle = rdr.ReadSingle();

            var player = client.Player;

            if (player.Inventory[0] == null || player.Inventory[1] == null || !player.GameServer.Resources.GameData.Items.TryGetValue((ushort)ContainerType, out var item))
            {
                client.Disconnect("Attempting to shoot a invalid item");
                return;
            }

            if (item.ObjectType == player.Inventory[0].ObjectType)
            {
                if (player.World.DisableShooting)
                {
                    client.Disconnect("Attempting to shoot in a disabled world");
                    return;
                }

                // create projectile and show other players
                var prjDesc = item.Projectiles[0]; //Assume only one
                var prj = player.PlayerShootProjectile(BulletId, prjDesc, item.ObjectType, Time, StartingPos, Angle);

                player.World.AddProjectile(prj);

                player.World.BroadcastIfVisibleExclude(new AllyShoot()
                {
                    OwnerId = player.Id,
                    Angle = Angle,
                    ContainerType = ContainerType,
                    BulletId = BulletId
                }, player, player);
                player.FameCounter.Shoot(prj);
                return;
            }

            // ability shoot handled by useitem
            if (item.ObjectType == player.Inventory[1].ObjectType)
            {
                if (player.World.DisableAbilities)
                    client.Disconnect("Attempting to activate ability in a disabled world");
                return; // ability shoot handled by useitem
            }

            System.Console.WriteLine($"{player.Name} has reached the end of handler");
        }
    }
}
