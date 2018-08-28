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
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace DbExtensions.Metadata {

   class AttributedMetaModel : MetaModel {

      ReaderWriterLock @lock = new ReaderWriterLock();
      Dictionary<Type, MetaType> metaTypes;
      Dictionary<Type, MetaTable> metaTables;

      internal override MappingSource MappingSource { get; }

      internal override Type ContextType { get; }

      internal override string DatabaseName { get; }

      [SuppressMessage("Microsoft.Usage", "CA2214:DoNotCallOverridableMethodsInConstructors")]
      internal AttributedMetaModel(MappingSource mappingSource, Type contextType) {

         this.MappingSource = mappingSource;
         this.ContextType = contextType;
         this.metaTypes = new Dictionary<Type, MetaType>();
         this.metaTables = new Dictionary<Type, MetaTable>();

         DatabaseAttribute[] das = (DatabaseAttribute[])this.ContextType.GetCustomAttributes(typeof(DatabaseAttribute), false);
         this.DatabaseName = (das != null && das.Length > 0) ? das[0].Name : this.ContextType.Name;
      }

      public override IEnumerable<MetaTable> GetTables() {

         @lock.AcquireReaderLock(Timeout.Infinite);

         try {
            return this.metaTables.Values.Where(x => x != null).Distinct();
         } finally {
            @lock.ReleaseReaderLock();
         }
      }

      public override MetaTable GetTable(Type rowType) {

         if (rowType == null) throw Error.ArgumentNull(nameof(rowType));

         MetaTable table;

         @lock.AcquireReaderLock(Timeout.Infinite);

         try {
            if (this.metaTables.TryGetValue(rowType, out table)) {
               return table;
            }
         } finally {
            @lock.ReleaseReaderLock();
         }

         @lock.AcquireWriterLock(Timeout.Infinite);

         try {
            table = GetTableNoLocks(rowType);
         } finally {
            @lock.ReleaseWriterLock();
         }

         return table;
      }

      internal MetaTable GetTableNoLocks(Type rowType) {

         MetaTable table;

         if (!this.metaTables.TryGetValue(rowType, out table)) {

            Type root = GetRoot(rowType) ?? rowType;
            TableAttribute[] attrs = (TableAttribute[])root.GetCustomAttributes(typeof(TableAttribute), true);

            if (attrs.Length == 0) {
               this.metaTables.Add(rowType, null);
            } else {

               if (!this.metaTables.TryGetValue(root, out table)) {

                  table = new AttributedMetaTable(this, attrs[0], root);

                  foreach (MetaType mt in table.RowType.InheritanceTypes) {
                     this.metaTables.Add(mt.Type, table);
                  }
               }

               // catch case of derived type that is not part of inheritance

               if (table.RowType.GetInheritanceType(rowType) == null) {
                  this.metaTables.Add(rowType, null);
                  return null;
               }
            }
         }

         return table;
      }

      static Type GetRoot(Type derivedType) {

         while (derivedType != null && derivedType != typeof(object)) {

            TableAttribute[] attrs = (TableAttribute[])derivedType.GetCustomAttributes(typeof(TableAttribute), false);

            if (attrs.Length > 0) {
               return derivedType;
            }

            derivedType = derivedType.BaseType;
         }

         return null;
      }

      public override MetaType GetMetaType(Type type) {

         if (type == null) throw Error.ArgumentNull(nameof(type));

         MetaType mtype = null;

         @lock.AcquireReaderLock(Timeout.Infinite);

         try {
            if (this.metaTypes.TryGetValue(type, out mtype)) {
               return mtype;
            }
         } finally {
            @lock.ReleaseReaderLock();
         }

         // Attributed meta model allows us to learn about tables we did not
         // statically know about

         MetaTable tab = GetTable(type);

         if (tab != null) {
            return tab.RowType.GetInheritanceType(type);
         }

         @lock.AcquireWriterLock(Timeout.Infinite);

         try {
            if (!this.metaTypes.TryGetValue(type, out mtype)) {
               mtype = new UnmappedType(this, type);
               this.metaTypes.Add(type, mtype);
            }
         } finally {
            @lock.ReleaseWriterLock();
         }

         return mtype;
      }
   }

   sealed class AttributedMetaTable : MetaTable {

      public override MetaModel Model { get; }

      public override string TableName { get; }

      public override MetaType RowType { get; }

      internal AttributedMetaTable(AttributedMetaModel model, TableAttribute attr, Type rowType) {

         this.Model = model;
         this.TableName = String.IsNullOrEmpty(attr.Name) ? rowType.Name : attr.Name;
         this.RowType = new AttributedRootType(model, this, rowType);
      }
   }

   sealed class AttributedRootType : AttributedMetaType {

      Dictionary<Type, MetaType> types;
      Dictionary<object, MetaType> codeMap;

      internal override bool HasInheritance => types != null;

      internal override ReadOnlyCollection<MetaType> InheritanceTypes { get; }

      internal override MetaType InheritanceDefault { get; }

      internal AttributedRootType(AttributedMetaModel model, AttributedMetaTable table, Type type)
         : base(model, table, type, null) {

         // check for inheritance and create all other types
         InheritanceMappingAttribute[] inheritanceInfo = (InheritanceMappingAttribute[])type.GetCustomAttributes(typeof(InheritanceMappingAttribute), true);

         if (inheritanceInfo.Length > 0) {

            if (this.Discriminator == null) {
               throw Error.NoDiscriminatorFound(type);
            }

            if (!MappingSystem.IsSupportedDiscriminatorType(this.Discriminator.Type)) {
               throw Error.DiscriminatorClrTypeNotSupported(this.Discriminator.DeclaringType.Name, this.Discriminator.Name, this.Discriminator.Type);
            }

            this.types = new Dictionary<Type, MetaType>();
            this.types.Add(type, this); // add self
            this.codeMap = new Dictionary<object, MetaType>();

            // initialize inheritance types

            foreach (InheritanceMappingAttribute attr in inheritanceInfo) {

               if (!type.IsAssignableFrom(attr.Type)) {
                  throw Error.InheritanceTypeDoesNotDeriveFromRoot(attr.Type, type);
               }

               if (attr.Type.IsAbstract) {
                  throw Error.AbstractClassAssignInheritanceDiscriminator(attr.Type);
               }

               AttributedMetaType mt = this.CreateInheritedType(type, attr.Type);

               if (attr.Code == null) {
                  throw Error.InheritanceCodeMayNotBeNull();
               }

               if (mt.inheritanceCode != null) {
                  throw Error.InheritanceTypeHasMultipleDiscriminators(attr.Type);
               }

               //object codeValue = DBConvert.ChangeType(*/attr.Code/*, this.Discriminator.Type);
               object codeValue = attr.Code;

               foreach (object d in codeMap.Keys) {

                  // if the keys are equal, or if they are both strings containing only spaces
                  // they are considered equal

                  if ((codeValue.GetType() == typeof(string)
                        && ((string)codeValue).Trim().Length == 0
                        && d.GetType() == typeof(string)
                        && ((string)d).Trim().Length == 0)
                     || object.Equals(d, codeValue)) {

                     throw Error.InheritanceCodeUsedForMultipleTypes(codeValue);
                  }
               }

               mt.inheritanceCode = codeValue;
               this.codeMap.Add(codeValue, mt);

               if (attr.IsDefault) {

                  if (this.InheritanceDefault != null) {
                     throw Error.InheritanceTypeHasMultipleDefaults(type);
                  }

                  this.InheritanceDefault = mt;
               }
            }

            if (this.InheritanceDefault == null) {
               throw Error.InheritanceHierarchyDoesNotDefineDefault(type);
            }
         }

         if (this.types != null) {
            this.InheritanceTypes = this.types.Values.ToList().AsReadOnly();
         } else {
            this.InheritanceTypes = new MetaType[] { this }.ToList().AsReadOnly();
         }

         Validate();
      }

      void Validate() {

         Dictionary<object, string> memberToColumn = new Dictionary<object, string>();

         foreach (MetaType type in this.InheritanceTypes) {

            if (type != this) {

               TableAttribute[] attrs = (TableAttribute[])type.Type.GetCustomAttributes(typeof(TableAttribute), false);

               if (attrs.Length > 0) {
                  throw Error.InheritanceSubTypeIsAlsoRoot(type.Type);
               }
            }

            foreach (MetaDataMember mem in type.PersistentDataMembers) {

               if (mem.IsDeclaredBy(type)) {

                  if (mem.IsDiscriminator && !this.HasInheritance) {
                     throw Error.NonInheritanceClassHasDiscriminator(type);
                  }

                  if (!mem.IsAssociation) {

                     // validate that no database column is mapped twice

                     if (!String.IsNullOrEmpty(mem.MappedName)) {

                        string column;
                        object dn = InheritanceRules.DistinguishedMemberName(mem.Member);

                        if (memberToColumn.TryGetValue(dn, out column)) {
                           if (column != mem.MappedName) {
                              throw Error.MemberMappedMoreThanOnce(mem.Member.Name);
                           }
                        } else {
                           memberToColumn.Add(dn, mem.MappedName);
                        }
                     }
                  }
               }
            }
         }
      }

      AttributedMetaType CreateInheritedType(Type root, Type type) {

         MetaType metaType;

         if (!this.types.TryGetValue(type, out metaType)) {

            metaType = new AttributedMetaType(this.Model, this.Table, type, this);
            this.types.Add(type, metaType);

            if (type != root && type.BaseType != typeof(object)) {
               CreateInheritedType(root, type.BaseType);
            }
         }

         return (AttributedMetaType)metaType;
      }

      internal override MetaType GetInheritanceType(Type type) {

         if (type == this.Type) {
            return this;
         }

         MetaType metaType = null;

         if (this.types != null) {
            this.types.TryGetValue(type, out metaType);
         }

         return metaType;
      }
   }

   class AttributedMetaType : MetaType {

      Dictionary<MetaPosition, MetaDataMember> dataMemberMap;
      ReadOnlyCollection<MetaDataMember> dataMembers;
      ReadOnlyCollection<MetaAssociation> associations;
      MetaDataMember dbGeneratedIdentity;
      MetaDataMember version;

      bool inheritanceBaseSet;
      MetaType inheritanceBase;
      internal object inheritanceCode;
      MetaDataMember discriminator;
      ReadOnlyCollection<MetaType> derivedTypes;

      object locktarget = new object(); // Hold locks on private object rather than public MetaType.

      public override MetaModel Model { get; }

      public override MetaTable Table { get; }

      public override Type Type { get; }

      public override MetaDataMember DBGeneratedIdentityMember => dbGeneratedIdentity;

      public override MetaDataMember VersionMember => version;

      public override string Name => Type.Name;

      public override bool IsEntity => Table?.RowType.IdentityMembers.Count > 0;

      public override bool CanInstantiate => !Type.IsAbstract && (this == InheritanceRoot || HasInheritanceCode);

      public override bool HasUpdateCheck => PersistentDataMembers.Any(m => m.UpdateCheck != UpdateCheck.Never);

      public override ReadOnlyCollection<MetaDataMember> DataMembers => dataMembers;

      public override ReadOnlyCollection<MetaDataMember> PersistentDataMembers { get; }

      public override ReadOnlyCollection<MetaDataMember> IdentityMembers { get; }

      public override ReadOnlyCollection<MetaAssociation> Associations {
         get {

            // LOCKING: Associations are late-expanded so that cycles are broken.

            if (associations == null) {
               lock (locktarget) {
                  if (associations == null) {
                     associations = DataMembers.Where(m => m.IsAssociation)
                        .Select(m => m.Association)
                        .ToList()
                        .AsReadOnly();
                  }
               }
            }

            return associations;
         }
      }

      internal override MetaType InheritanceRoot { get; }

      internal override MetaDataMember Discriminator => discriminator;

      internal override bool HasInheritance => InheritanceRoot.HasInheritance;

      internal override bool HasInheritanceCode => InheritanceCode != null;

      internal override object InheritanceCode => inheritanceCode;

      internal override MetaType InheritanceDefault => InheritanceRoot.InheritanceDefault;

      internal override bool IsInheritanceDefault => InheritanceDefault == this;

      internal override ReadOnlyCollection<MetaType> InheritanceTypes => InheritanceRoot.InheritanceTypes;

      internal override MetaType InheritanceBase {
         get {

            // LOCKING: Cannot initialize at construction

            if (!inheritanceBaseSet
               && inheritanceBase == null) {

               lock (locktarget) {
                  if (inheritanceBase == null) {
                     inheritanceBase = InheritanceBaseFinder.FindBase(this);
                     inheritanceBaseSet = true;
                  }
               }
            }

            return inheritanceBase;
         }
      }

      internal override ReadOnlyCollection<MetaType> DerivedTypes {
         get {

            // LOCKING: Cannot initialize at construction because derived types
            // won't exist yet.

            if (derivedTypes == null) {
               lock (locktarget) {
                  if (derivedTypes == null) {

                     var dTypes = new List<MetaType>();

                     foreach (MetaType mt in InheritanceTypes) {
                        if (mt.Type.BaseType == Type) {
                           dTypes.Add(mt);
                        }
                     }

                     derivedTypes = dTypes.AsReadOnly();
                  }
               }
            }

            return derivedTypes;
         }
      }

      [SuppressMessage("Microsoft.Usage", "CA2214:DoNotCallOverridableMethodsInConstructors")]
      internal AttributedMetaType(MetaModel model, MetaTable table, Type type, MetaType inheritanceRoot) {

         this.Model = model;
         this.Table = table;
         this.Type = type;
         this.InheritanceRoot = inheritanceRoot ?? this;

         // Not lazy-loading to simplify locking and enhance performance 
         // (because no lock will be required for the common read scenario).

         

         this.IdentityMembers = this.DataMembers.Where(m => m.IsPrimaryKey).ToList().AsReadOnly();
         this.PersistentDataMembers = this.DataMembers.Where(m => m.IsPersistent).ToList().AsReadOnly();
      }

      void ValidatePrimaryKeyMember(MetaDataMember mm) {

         //if the type is a sub-type, no member declared in the type can be primary key

         if (mm.IsPrimaryKey
            && this.InheritanceRoot != this
            && mm.Member.DeclaringType == this.Type) {

            throw (Error.PrimaryKeyInSubTypeNotSupported(this.Type.Name, mm.Name));
         }
      }


      void InitSpecialMember(MetaDataMember mm) {

         // Can only have one auto gen member that is also an identity member,
         // except if that member is a computed column (since they are implicitly auto gen)

         if (mm.IsDbGenerated
            && mm.IsPrimaryKey
            && String.IsNullOrEmpty(mm.Expression)) {

            if (this.dbGeneratedIdentity != null) {
               throw Error.TwoMembersMarkedAsPrimaryKeyAndDBGenerated(mm.Member, this.dbGeneratedIdentity.Member);
            }

            this.dbGeneratedIdentity = mm;
         }

         if (mm.IsPrimaryKey
            && !MappingSystem.IsSupportedIdentityType(mm.Type)) {

            throw Error.IdentityClrTypeNotSupported(mm.DeclaringType, mm.Name, mm.Type);
         }

         if (mm.IsVersion) {

            if (this.version != null) {
               throw Error.TwoMembersMarkedAsRowVersion(mm.Member, this.version.Member);
            }

            this.version = mm;
         }

         if (mm.IsDiscriminator) {

            if (this.discriminator != null) {
               throw Error.TwoMembersMarkedAsInheritanceDiscriminator(mm.Member, this.discriminator.Member);
            }

            this.discriminator = mm;
         }
      }

      public override MetaDataMember GetDataMember(MemberInfo mi) {

         if (mi == null) throw Error.ArgumentNull(nameof(mi));

         MetaDataMember mm = null;

         if (this.dataMemberMap.TryGetValue(new MetaPosition(mi), out mm)) {
            return mm;
         }

         // DON'T look to see if we are trying to get a member from an inherited type.
         // The calling code should know to look in the inherited type.

         if (mi.DeclaringType.IsInterface) {
            throw Error.MappingOfInterfacesMemberIsNotSupported(mi.DeclaringType.Name, mi.Name);
         }

         // the member is not mapped in the base class

         throw Error.UnmappedClassMember(mi.DeclaringType.Name, mi.Name);
      }

      internal override MetaType GetInheritanceType(Type inheritanceType) {

         if (inheritanceType == this.Type) {
            return this;
         }

         return this.InheritanceRoot.GetInheritanceType(inheritanceType);
      }

      internal override MetaType GetTypeForInheritanceCode(object key) {

         if (this.InheritanceRoot.Discriminator.Type == typeof(string)) {

            string skey = (string)key;

            foreach (MetaType mt in this.InheritanceRoot.InheritanceTypes) {

               if (String.Compare((string)mt.InheritanceCode, skey, StringComparison.OrdinalIgnoreCase) == 0) {
                  return mt;
               }
            }

         } else {

            foreach (MetaType mt in this.InheritanceRoot.InheritanceTypes) {
               if (Object.Equals(mt.InheritanceCode, key)) {
                  return mt;
               }
            }
         }

         return null;
      }

      public override string ToString() {
         return this.Name;
      }
   }


   class AttributedMetaAssociation : MetaAssociationImpl {

      public override MetaType OtherType { get; }

      public override MetaDataMember ThisMember { get; }

      public override MetaDataMember OtherMember { get; }

      public override ReadOnlyCollection<MetaDataMember> ThisKey { get; }

      public override ReadOnlyCollection<MetaDataMember> OtherKey { get; }

      public override bool ThisKeyIsPrimaryKey { get; }

      public override bool OtherKeyIsPrimaryKey { get; }

      public override bool IsMany { get; }

      public override bool IsForeignKey { get; }

      public override bool IsUnique { get; }

      public override bool IsNullable { get; }

      public override string DeleteRule { get; }

      public override bool DeleteOnNull { get; }

   }

   abstract class MetaAssociationImpl : MetaAssociation {

      static char[] keySeparators = new char[] { ',' };

      /// <summary>
      /// Given a MetaType and a set of key fields, return the set of MetaDataMembers
      /// corresponding to the key.
      /// </summary>

      protected static ReadOnlyCollection<MetaDataMember> MakeKeys(MetaType mtype, string keyFields) {

         string[] names = keyFields.Split(keySeparators);

         var members = new MetaDataMember[names.Length];

         for (int i = 0; i < names.Length; i++) {

            names[i] = names[i].Trim();

            MemberInfo[] rmis = mtype.Type.GetMember(names[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (rmis == null || rmis.Length != 1) {
               throw Error.BadKeyMember(names[i], keyFields, mtype.Name);
            }

            members[i] = mtype.GetDataMember(rmis[0]);

            if (members[i] == null) {
               throw Error.BadKeyMember(names[i], keyFields, mtype.Name);
            }
         }

         return new List<MetaDataMember>(members).AsReadOnly();
      }

      /// <summary>
      /// Compare two sets of keys for equality.
      /// </summary>

      protected static bool AreEqual(IEnumerable<MetaDataMember> key1, IEnumerable<MetaDataMember> key2) {

         using (IEnumerator<MetaDataMember> e1 = key1.GetEnumerator()) {
            using (IEnumerator<MetaDataMember> e2 = key2.GetEnumerator()) {

               bool m1, m2;

               for (m1 = e1.MoveNext(), m2 = e2.MoveNext(); m1 && m2; m1 = e1.MoveNext(), m2 = e2.MoveNext()) {
                  if (e1.Current != e2.Current) {
                     return false;
                  }
               }

               if (m1 != m2) {
                  return false;
               }
            }
         }

         return true;
      }

      public override string ToString() {
         return String.Format(CultureInfo.InvariantCulture, "{0} ->{1} {2}", this.ThisMember.DeclaringType.Name, this.IsMany ? "*" : "", this.OtherType.Name);
      }
   }

   sealed class UnmappedType : MetaType {

      static ReadOnlyCollection<MetaType> _emptyTypes = new List<MetaType>().AsReadOnly();
      static ReadOnlyCollection<MetaDataMember> _emptyDataMembers = new List<MetaDataMember>().AsReadOnly();
      static ReadOnlyCollection<MetaAssociation> _emptyAssociations = new List<MetaAssociation>().AsReadOnly();

      Dictionary<object, MetaDataMember> dataMemberMap;
      ReadOnlyCollection<MetaDataMember> dataMembers;
      ReadOnlyCollection<MetaType> inheritanceTypes;
      object locktarget = new object(); // Hold locks on private object rather than public MetaType.

      public override MetaModel Model { get; }

      public override Type Type { get; }

      public override MetaTable Table => null;

      public override string Name => Type.Name;

      public override bool IsEntity => false;

      public override bool CanInstantiate => !Type.IsAbstract;

      public override MetaDataMember DBGeneratedIdentityMember => null;

      public override MetaDataMember VersionMember => null;

      internal override MetaDataMember Discriminator => null;

      public override bool HasUpdateCheck => false;

      public override ReadOnlyCollection<MetaDataMember> DataMembers {
         get {
            return dataMembers;
         }
      }

      public override ReadOnlyCollection<MetaDataMember> PersistentDataMembers => _emptyDataMembers;

      public override ReadOnlyCollection<MetaDataMember> IdentityMembers {
         get {
            return dataMembers;
         }
      }

      public override ReadOnlyCollection<MetaAssociation> Associations => _emptyAssociations;

      internal override ReadOnlyCollection<MetaType> InheritanceTypes {
         get {

            if (inheritanceTypes == null) {
               lock (locktarget) {
                  if (inheritanceTypes == null) {
                     inheritanceTypes = new MetaType[] { this }.ToList().AsReadOnly();
                  }
               }
            }

            return inheritanceTypes;
         }
      }

      internal override ReadOnlyCollection<MetaType> DerivedTypes => _emptyTypes;

      internal override bool HasInheritance => false;

      internal override bool HasInheritanceCode => false;

      internal override object InheritanceCode => null;

      internal override MetaType InheritanceRoot => this;

      internal override MetaType InheritanceBase => null;

      internal override MetaType InheritanceDefault => null;

      internal override bool IsInheritanceDefault => false;

      internal UnmappedType(MetaModel model, Type type) {

         this.Model = model;
         this.Type = type;
      }

      internal override MetaType GetInheritanceType(Type inheritanceType) {

         if (inheritanceType == this.Type) {
            return this;
         }

         return null;
      }

      internal override MetaType GetTypeForInheritanceCode(object key) {
         return null;
      }

      public override MetaDataMember GetDataMember(MemberInfo mi) {

         if (mi == null) throw Error.ArgumentNull(nameof(mi));


         if (this.dataMemberMap == null) {
            lock (this.locktarget) {
               if (this.dataMemberMap == null) {

                  var map = new Dictionary<object, MetaDataMember>();

                  foreach (MetaDataMember mm in this.dataMembers) {
                     map.Add(InheritanceRules.DistinguishedMemberName(mm.Member), mm);
                  }

                  this.dataMemberMap = map;
               }
            }
         }

         object dn = InheritanceRules.DistinguishedMemberName(mi);

         MetaDataMember mdm;
         this.dataMemberMap.TryGetValue(dn, out mdm);

         return mdm;
      }


      public override string ToString() {
         return this.Name;
      }
   }


   static class InheritanceBaseFinder {

      internal static MetaType FindBase(MetaType derivedType) {

         if (derivedType.Type == typeof(object)) {
            return null;
         }

         var clrType = derivedType.Type; // start
         var rootClrType = derivedType.InheritanceRoot.Type; // end
         var metaTable = derivedType.Table;
         MetaType metaType = null;

         while (true) {

            if (clrType == typeof(object)
               || clrType == rootClrType) {

               return null;
            }

            clrType = clrType.BaseType;
            metaType = derivedType.InheritanceRoot.GetInheritanceType(clrType);

            if (metaType != null) {
               return metaType;
            }
         }
      }
   }

   /// <summary>
   /// This class defines the rules for inheritance behaviors. The rules:
   /// 
   ///  (1) The same field may not be mapped to different database columns.    
   ///      The DistinguishedMemberName and AreSameMember methods describe what 'same' means between two MemberInfos.
   ///  (2) Discriminators held in fixed-length fields in the database don't need
   ///      to be manually padded in inheritance mapping [InheritanceMapping(Code='x')]. 
   ///  
   /// </summary>

   static class InheritanceRules {

      /// <summary>
      /// Creates a name that is the same when the member should be considered 'same'
      /// for the purposes of the inheritance feature.
      /// </summary>

      internal static object DistinguishedMemberName(MemberInfo mi) {

         PropertyInfo pi = mi as PropertyInfo;
         FieldInfo fi = mi as FieldInfo;

         if (fi != null) {

            // Human readable variant:
            // return "fi:" + mi.Name + ":" + mi.DeclaringType;
            return new MetaPosition(mi);

         } else if (pi != null) {

            MethodInfo meth = null;

            if (pi.CanRead) {
               meth = pi.GetGetMethod();
            }

            if (meth == null && pi.CanWrite) {
               meth = pi.GetSetMethod();
            }

            bool isVirtual = meth != null && meth.IsVirtual;

            // Human readable variant:
            // return "pi:" + mi.Name + ":" + (isVirtual ? "virtual" : mi.DeclaringType.ToString());

            if (isVirtual) {
               return mi.Name;
            } else {
               return new MetaPosition(mi);
            }

         } else {
            throw Error.ArgumentOutOfRange(nameof(mi));
         }
      }

      /// <summary>
      /// Compares two MemberInfos for 'same-ness'.
      /// </summary>

      internal static bool AreSameMember(MemberInfo mi1, MemberInfo mi2) {
         return DistinguishedMemberName(mi1).Equals(DistinguishedMemberName(mi2));
      }
   }
}
