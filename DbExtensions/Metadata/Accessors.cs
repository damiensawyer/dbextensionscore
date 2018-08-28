// Copyright 2016-2018 Max Toro Q.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

#region Based on code from .NET Framework
#endregion

using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace DbExtensions.Metadata {

   delegate V DGet<T, V>(T t);
   delegate void DSet<T, V>(T t, V v);
   delegate void DRSet<T, V>(ref T t, V v);


   static class PropertyAccessor {


      class Accessor<T, V, V2> : MetaAccessor<T, V> where V2 : V {

         PropertyInfo pi;
         DGet<T, V> dget;
         DSet<T, V> dset;
         DRSet<T, V> drset;
         MetaAccessor<T, V2> storage;

         internal Accessor(PropertyInfo pi, DGet<T, V> dget, DSet<T, V> dset, DRSet<T, V> drset, MetaAccessor<T, V2> storage) {

            this.pi = pi;
            this.dget = dget;
            this.dset = dset;
            this.drset = drset;
            this.storage = storage;
         }

         public override V GetValue(T instance) {
            return this.dget(instance);
         }

         public override void SetValue(ref T instance, V value) {

            if (this.dset != null) {
               this.dset(instance, value);

            } else if (this.drset != null) {
               this.drset(ref instance, value);

            } else if (this.storage != null) {
               this.storage.SetValue(ref instance, (V2)value);

            } else {
               throw Error.UnableToAssignValueToReadonlyProperty(this.pi);
            }
         }
      }
   }
}
