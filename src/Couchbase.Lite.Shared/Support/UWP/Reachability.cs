﻿// 
// Reachability.cs
// 
// Copyright (c) 2017 Couchbase, Inc All rights reserved.
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
// http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// 
#if UAP10_0_16299 || WINDOWS_UWP || NET6_0_WINDOWS10_0_19041_0
using System;
using System.Threading.Tasks;

using Couchbase.Lite.DI;
using Couchbase.Lite.Sync;
using Windows.Networking.Connectivity;

namespace Couchbase.Lite.Support
{
    [CouchbaseDependency(Transient = true)]
    internal sealed class Reachability : IReachability
    {
        #region Variables

        public event EventHandler<NetworkReachabilityChangeEventArgs> StatusChanged;

        #endregion
        
        #region Properties
        
        public Uri Url { get; set; }
        
        #endregion

        #region Private Methods

        private void OnNetworkStatusChanged(object sender)
        {
            var connection = NetworkInformation.GetInternetConnectionProfile();
            var status = connection == null
                ? NetworkReachabilityStatus.Unreachable
                : NetworkReachabilityStatus.Reachable;

			Task.Factory.StartNew(() => { StatusChanged?.Invoke(this, new NetworkReachabilityChangeEventArgs(status)); });
        }

        #endregion

        #region IReachability

        public void Start()
        {
            NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
        }

        public void Stop()
        {
            NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
        }

        #endregion
    }
}
#endif