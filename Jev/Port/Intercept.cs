using System;
using Microsoft.Xna.Framework;

namespace TerraBlind.JevariaCombat;

public static class Intercept
{
    public static bool TrySolve(Vector2 origin, Vector2 target, Vector2 targetVelocity,
        float shotSpeed, out Vector2 aimPoint)
    {
        aimPoint = target;
        if (shotSpeed <= 0f) return false;

        Vector2 offset = target - origin;
        double a = targetVelocity.LengthSquared() - shotSpeed * shotSpeed;
        double b = 2d * Vector2.Dot(offset, targetVelocity);
        double c = offset.LengthSquared();
        double time;

        if (Math.Abs(a) < 1e-6)
        {
            if (Math.Abs(b) < 1e-6) return c == 0;
            time = -c / b;
        }
        else
        {
            double discriminant = b * b - 4d * a * c;
            if (discriminant < 0d) return false;
            double root = Math.Sqrt(discriminant);
            double first = (-b - root) / (2d * a);
            double second = (-b + root) / (2d * a);
            time = first > 0d ? first : second;
            if (second > 0d && second < time) time = second;
        }

        if (time <= 0d || double.IsNaN(time) || double.IsInfinity(time)) return false;
        aimPoint = target + targetVelocity * (float)time;
        return true;
    }
}

