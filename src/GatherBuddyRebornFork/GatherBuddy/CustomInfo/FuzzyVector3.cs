using GatherBuddy.Plugin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using ECommons.GameHelpers;
using ECommons.MathHelpers;

namespace GatherBuddy.CustomInfo
{
    public static class VectorExtensions
    {
        public static float DistanceToPlayer(this Vector3 vector)
        {
            var distance = Vector3.Distance(vector, Player.Object.Position);
            return distance;
        }

        public static float DistanceToPlayer2(this Vector3 vector)
        {
            var vector2 = vector.ToVector2();
            var distance = Vector2.Distance(vector2, Player.Object.Position.ToVector2());
            return distance;
        }
    }
}
