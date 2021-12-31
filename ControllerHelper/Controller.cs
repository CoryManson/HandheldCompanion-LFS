﻿using System;

namespace ControllerHelper
{
    public class Controller
    {
        public string ProductName;
        public Guid ProductGuid;
        public Guid InstanceGuid;
        public int ProductIndex;

        public Controller(string ProductName, Guid InstanceGuid, Guid ProductGuid, int ProductIndex)
        {
            this.ProductName = ProductName;
            this.InstanceGuid = InstanceGuid;
            this.ProductGuid = ProductGuid;
            this.ProductIndex = ProductIndex;
        }

        public override string ToString()
        {
            return this.ProductName;
        }
    }
}
