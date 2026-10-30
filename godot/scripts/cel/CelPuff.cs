namespace TankSpriteTest;

/// <summary>
/// The 3D effects' hash for their puffs and tongues (<see cref="CelBurn"/>,
/// <see cref="CelExhaust"/>, <see cref="CelShot"/>, ...): a puff's seed, size
/// and way off its index and lap, the same on every run. The burning column's
/// puffs were once drawn from here as spheres, each inked on its own - a heap
/// of balls; they are one cloud now (<see cref="CelCloud"/>).
/// </summary>
public static class CelPuff
{
    public static float Hash(int k, int salt)
    {
        unchecked
        {
            uint h = (uint)(k * 73856093) ^ (uint)(salt * 19349663) ^ 0x9E3779B9u;
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777215.0f;
        }
    }
}
