﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using ProtoBuf.Meta;
using SerializationException = ProtoBuf.Wcf.Exceptions.SerializationException;

namespace ProtoBuf.Wcf.Serialization
{
    public sealed class ProtoBufModelGenerator
    {
        #region Construction

        public ProtoBufModelGenerator(Type type)
        {
            _targetType = type;
            _typeMetaData = new TypeMetaData();
            _usePreDefinedFieldNumbers = false;
        }

        public ProtoBufModelGenerator(Type type, TypeMetaData typeMetaData)
        {
            _targetType = type;
            _typeMetaData = typeMetaData;
            _usePreDefinedFieldNumbers = true;
        }

        #endregion

        #region Configuration

        #region Fields

        private Type _targetType;
        private readonly TypeMetaData _typeMetaData;
        private readonly bool _usePreDefinedFieldNumbers;

        private readonly ISet<string> _heirarchySet = new HashSet<string>();
        private readonly ISet<Type> _typeSet = new HashSet<Type>();

        #endregion

        public ModelInfo ConfigureType(Type type, bool recursive, bool configureChildren)
        {
            var method = this.GetType().GetMethod("ConfigureTypeInternal", BindingFlags.Instance | BindingFlags.NonPublic);
            var genericMethod = method.MakeGenericMethod(type);
            var info = genericMethod.Invoke(this, new object[] { true, true });

            return (ModelInfo)info;
        }

        private ModelInfo ConfigureTypeInternal<T>(bool recursive, bool configureChildren)
        {
            var targetType = typeof(T);

            var model = GetTypeModel(targetType);

            ConfigureType<T>(targetType, recursive, configureChildren, new HashSet<Type>(),
                model, targetType);

            return new ModelInfo(model, _typeMetaData);
        }

        private void ConfigureType<T>(Type type, bool recursive, bool configureChildren,
            ISet<Type> navigatedTypes, RuntimeTypeModel model, Type originalType)
        {
            if (!IsValidType(type))
                return;

            model.Add(type, false);

            var baseTypes = GetRecursiveBase(type).ToArray();

            for (int i = 0; i < baseTypes.Length; i++)
            {
                var targetBaseType = baseTypes[i];

                if (IsValidType(targetBaseType))
                    model.Add(targetBaseType, false);

                if (IsValidType(targetBaseType) && targetBaseType.GetCustomAttribute<DataContractAttribute>() == null)
                    throw new InvalidOperationException("type does not have DataContract attribute: " + targetBaseType.AssemblyQualifiedName);

                var targetChild = i == 0 ? type : baseTypes[i - 1];

                ConfigureSubType(targetBaseType, targetChild, model, originalType);

                if (!recursive)
                    break;
            }

            ConfigureTypeOnModel(type, model, originalType);

            if (configureChildren && IsValidType(type))
            {
                var children = GetReferencedTypes(type).Concat(GetChildren(type, originalType)).Distinct().ToList();

                if (children.Any(x => IsValidType(x) && x.GetCustomAttribute<DataContractAttribute>() == null))
                {
                    throw new InvalidOperationException("type does not have DataContract attribute: " +
                        children.Where(x => IsValidType(x) && x.GetCustomAttribute<DataContractAttribute>() == null).Select(x => x.AssemblyQualifiedName)
                        .Aggregate((s, s1) => s + Environment.NewLine + s1));
                }

                foreach (var child in children)
                {
                    if (!navigatedTypes.Add(child))
                        continue;

                    ConfigureType<NullType>(child, recursive, true, navigatedTypes, model, originalType);
                }
            }

            if (typeof(T) != typeof(NullType))
                PrepareSerializer<T>(model);
        }

        private RuntimeTypeModel GetTypeModel(Type type)
        {
            var model = TypeModel.Create();
            model.AutoAddMissingTypes = true;
            model.UseImplicitZeroDefaults = true;
            model.AutoCompile = true;

            return model;
        }

        private class NullType
        { }

        private void ConfigureSubType(Type baseType, Type type, RuntimeTypeModel model, Type originalType)
        {
            if (type == null)
                throw new ArgumentNullException("type");

            if (IsValidType(baseType) && _heirarchySet.Add(GetHashKey(originalType, type)))
            {
                model.Add(baseType, false).AddSubType(GetAndStoreBaseFieldNumber(baseType, GetChildren(baseType, originalType).ToArray(), type), type);
            }
        }

        private void ConfigureTypeOnModel(Type type, RuntimeTypeModel model, Type originalType)
        {
            if (type == null)
                throw new ArgumentNullException("type");

            if (!IsValidType(type) || !_typeSet.Add(type))
                return;

            var baseType = type.BaseType;

            ConfigureTypeOnModel(baseType, model, originalType);

            var fields = GetValidProperties(type).Concat(GetValidFields(type));

            var metaData = _typeMetaData;

            var fieldNumber = _usePreDefinedFieldNumbers ? 0 :
                metaData.GetMaxFieldNumber(GetTypeNameSpace(baseType), GetTypeName(baseType));

            foreach (var fieldInfo in fields)
            {
                var typeNamespace = GetTypeNameSpace(type);

                var typeName = GetTypeName(type);

                var fieldName = fieldInfo.Name;

                if (!_usePreDefinedFieldNumbers)
                {
                    fieldNumber++;
                    metaData.StoreFieldNumber(typeNamespace, typeName, fieldName, fieldNumber);
                }
                else
                {
                    int? tempFieldNumber;

                    if (!metaData.GetFieldNumber(typeNamespace,
                        typeName, fieldName, out tempFieldNumber))
                        continue;

                    fieldNumber = tempFieldNumber.Value;
                }

                model.Add(type, false).AddField(fieldNumber, fieldName);
            }
        }

        private string GetTypeNameSpace(Type type)
        {
            var attribute = type.GetCustomAttribute<DataContractAttribute>();

            return attribute == null ? string.Empty : attribute.Namespace;
        }

        private string GetTypeName(Type type)
        {
            return type.Name;
        }

        private string GetHashKey(Type type1, Type type2)
        {
            return type1.AssemblyQualifiedName + "-" + type2.AssemblyQualifiedName;
        }

        private readonly IDictionary<Type, SortedList<string, string>> _typeDic = new Dictionary<Type, SortedList<string, string>>();

        private int GetAndStoreBaseFieldNumber(Type baseType, Type[] subTypes, Type subType)
        {
            var typeNamespace = GetTypeNameSpace(subType);

            var typeName = GetTypeName(subType);

            int? number;

            if (_usePreDefinedFieldNumbers &&
                _typeMetaData.GetBaseNumber(typeNamespace, typeName, out number))
            {
                return number.Value;
            }

            SortedList<string, string> sortedList;

            var bases = GetRecursiveBase(subType).ToArray();

            var baseMost = subType;
            if (bases.Any())
                baseMost = bases.Last();

            if (_typeDic.ContainsKey(baseMost))
                sortedList = _typeDic[baseMost];
            else
            {
                sortedList = new SortedList<string, string>(subTypes.Length);

                foreach (var type in subTypes)
                {
                    sortedList.Add(type.FullName, type.FullName);
                }
                _typeDic[baseMost] = sortedList;
            }

            number = CalculateFieldNumber(Math.Abs(baseMost.FullName.GetHashCode()),
                                          sortedList.IndexOfValue(subType.FullName));

            _typeMetaData.StoreBaseNumber(typeNamespace, typeName, number.Value);

            return number.Value;
        }

        private int CalculateFieldNumber(int baseNumber, int child)
        {
            return checked(Normalize(baseNumber) + (child * 4) + 4);
        }

        private int Normalize(int number)
        {
            const int nFactor = 23827;

            return Math.Abs(number % nFactor);
        }

        private bool IsValidType(Type type)
        {
            return type != null && type != typeof(object) && type != typeof(ValueType)
                   && type != typeof(Enum)
                   && type.Namespace != null
                   && type.IsArray == false
                   && !type.Namespace.StartsWith("System") &&
                   type.GetCustomAttribute<ProtoIgnoreAttribute>() == null
                   && type.GetCustomAttribute<DataContractAttribute>() != null;
        }

        private void PrepareSerializer<T>(RuntimeTypeModel model)
        {
            model.CompileInPlace();

            try
            {
                if (_targetType != typeof(TypeMetaData))
                    model.Compile();
            }
            catch (InvalidOperationException ex)
            {
                throw new SerializationException(
                   string.Format(
                   "The model {0} could not be serialized, this could be because private members in the type (or down its graph) are decorated with the 'DataMember attribute', check inner exception for more details.",
                   _targetType.FullName), ex);
            }

        }

        private IEnumerable<Type> GetReferencedTypes(Type type)
        {
            var fields = GetValidFields(type).Concat(GetValidProperties(type));

            var baseTypes = GetRecursiveBase(type);

            foreach (var baseType in baseTypes)
            {
                fields = fields.Concat(GetValidFields(baseType)).Concat(GetValidProperties(baseType));
            }

            return fields
                .Where(x => x.GetCustomAttribute<ProtoIgnoreAttribute>() == null &&
                    x.GetCustomAttribute<DataMemberAttribute>() != null)
                .SelectMany(x => GetDetailedTypes(GetTypeFromMemberInfo(x)))
                .Distinct();
        }

        private Type GetTypeFromMemberInfo(MemberInfo memberInfo)
        {
            var propertyType = memberInfo as PropertyInfo;

            if (propertyType != null)
                return propertyType.PropertyType;

            var fieldType = memberInfo as FieldInfo;

            if (fieldType != null)
                return fieldType.FieldType;

            throw new ArgumentOutOfRangeException("memberInfo", "memberinfo was of unexpected type - " + memberInfo.GetType());
        }

        private static IEnumerable<MemberInfo> GetValidFields(Type type)
        {
            return type
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(x => x.GetCustomAttribute<DataMemberAttribute>() != null);
        }

        private static IEnumerable<MemberInfo> GetValidProperties(Type type)
        {
            return type
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(x => x.GetCustomAttribute<DataMemberAttribute>() != null);
        }

        private IEnumerable<Type> GetDetailedTypes(Type type)
        {
            if (type.IsGenericType)
            {
                foreach (var genericTypeArgument in type.GenericTypeArguments)
                {
                    yield return genericTypeArgument;
                }
            }

            yield return type;
        }

        private IEnumerable<Type> GetChildren(Type baseType, Type originalType)
        {
            if (baseType == null)
                throw new ArgumentNullException("baseType");

            var targetAssemblies = GetTargetAssemblies(originalType);

            var targets = targetAssemblies.SelectMany(x => x.GetTypes());

            var list = new List<Type>();

            foreach (var type in targets)
            {
                if (baseType == type)
                    continue;

                if (baseType.IsAssignableFrom(type))
                {
                    var parameterlessConstructor = type.GetConstructor(Type.EmptyTypes);

                    if (parameterlessConstructor != null)
                    {
                        list.AddRange(GetChildren(type, originalType));
                        list.Add(type);
                    }
                }
            }
            list.AddRange(GetRecursiveBase(baseType));

            return list.Distinct();
        }

        private static readonly string[] TypeExclusions = new[] { "Microsoft", "System", "mscorlib" };
        private IEnumerable<Assembly> GetTargetAssemblies(Type originalType)
        {
            var validAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(x => !TypeExclusions.Any(y => x.FullName.StartsWith(y)));

            return validAssemblies;
        }

        private IEnumerable<Type> GetRecursiveBase(Type type)
        {
            var lType = type;

            while (IsValidType(lType.BaseType))
            {
                yield return lType.BaseType;

                lType = lType.BaseType;
            }
        }

        #endregion
    }
}