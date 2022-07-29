﻿using common;
using common.resources;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using wServer.core.worlds;
using wServer.utils;

namespace wServer.core.objects
{
    public class Portal : StaticObject
    {
        public World WorldInstance;

        private SV<bool> _usable;
        public readonly PortalDesc PortalDescr;
        
        public Portal(CoreServerManager manager, ushort objType, int? life) : base(manager, ValidatePortal(manager, objType), life, false, true, false)
        {
            _usable = new SV<bool>(this, StatDataType.PortalUsable, true);

            PortalDescr = manager.Resources.GameData.Portals[ObjectType];
            Locked = PortalDescr.Locked;
        }

        public bool Locked { get; private set; }
        public bool Usable { get => _usable.GetValue(); set => _usable.SetValue(value); }

        public override bool HitByProjectile(Projectile projectile, TickTime time) => false;

        protected override void ExportStats(IDictionary<StatDataType, object> stats)
        {
            stats[StatDataType.PortalUsable] = Usable ? 1 : 0;

            base.ExportStats(stats);
        }

        private static ushort ValidatePortal(CoreServerManager manager, ushort objType)
        {
            var portals = manager.Resources.GameData.Portals;

            if (!portals.ContainsKey(objType))
            {
                SLogger.Instance.Warn($"Portal {objType.To4Hex()} does not exist. Using Portal of Cowardice.");

                objType = 0x0703; // default to Portal of Cowardice
            }

            return objType;
        }
    }
}
