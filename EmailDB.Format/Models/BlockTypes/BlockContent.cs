﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format.Models.BlockTypes;

public abstract class BlockContent 
{
    public abstract BlockType GetBlockType();
}
