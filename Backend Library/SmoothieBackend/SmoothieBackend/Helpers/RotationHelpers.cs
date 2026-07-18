using WolvenKit.RED4.Types;

namespace SmoothieBackend.Helpers;

public static class RotationHelpers
{
    public static EulerAngles ToEulerAnglesRadian(this Quaternion q)
    {
        var m = GetMatrix(q);

        var euler = new EulerAngles();

        euler.Pitch = (float)Math.Asin(Clamp(m[2, 1], -1, 1));
        if (Math.Abs(m[2, 1]) < 0.99999)
        {
            euler.Yaw = (float)Math.Atan2(-m[0, 1], m[1, 1]);
            euler.Roll = (float)Math.Atan2(-m[2, 0], m[2, 2]);
        }
        else
        {
            euler.Yaw = (float)Math.Atan2(m[1, 0], m[0, 0]);
            euler.Roll = 0;
        }

        return euler;
    }
    
    private static double[,] GetMatrix(Quaternion q)
    {
        double w = q.R;
        double x = q.I;
        double y = q.J;
        double z = q.K;

        double[,] m = new double[3,3];

        m[0, 0] = 1 - 2 * (y * y + z * z);
        m[0, 1] = 2 * (x * y - z * w);
        m[0, 2] = 2 * (x * z + y * w);

        m[1, 0] = 2 * (x * y + z * w);
        m[1, 1] = 1 - 2 * (x * x + z * z);
        m[1, 2] = 2 * (y * z - x * w);

        m[2, 0] = 2 * (x * z - y * w);
        m[2, 1] = 2 * (y * z + x * w);
        m[2, 2] = 1 - 2 * (x * x + y * y);
        return m;
    }
    
    private static double Clamp(double val, double min, double max)
        => Math.Max(min, Math.Min(max, val));
}