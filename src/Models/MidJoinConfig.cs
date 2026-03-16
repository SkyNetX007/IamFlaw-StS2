namespace IamFlaw.Models;

public class MidJoinConfig
{
    public bool Enabled { get; set; } = true;
    public int DefaultCopySourceIndex { get; set; } = 0;
    public int MaxPlayers { get; set; } = 4;
}
