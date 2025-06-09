﻿namespace Tenray.ZoneTree.Segments.Block;

public sealed class SingleBlockPin
{
    public DecompressedBlock Device;

    public bool ContributeToTheBlockCache;

    public SingleBlockPin(DecompressedBlock device)
    {
        Device = device;
    }

    public void SetDevice(DecompressedBlock device) { Device = device; }
}


