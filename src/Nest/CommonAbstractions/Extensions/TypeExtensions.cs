﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Nest
{
	internal static class TypeExtensions
	{
		public delegate T ObjectActivator<out T>(params object[] args);

		private static readonly MethodInfo GetActivatorMethodInfo =
			typeof(TypeExtensions).GetMethod(nameof(GetActivator), BindingFlags.Static | BindingFlags.NonPublic);

		private static readonly ConcurrentDictionary<string, ObjectActivator<object>> CachedActivators =
			new ConcurrentDictionary<string, ObjectActivator<object>>();

		private static readonly ConcurrentDictionary<string, Type> CachedGenericClosedTypes =
			new ConcurrentDictionary<string, Type>();

		private static readonly ConcurrentDictionary<Type, IList<JsonProperty>> CachedTypeProperties =
			new ConcurrentDictionary<Type, IList<JsonProperty>>();

		private static readonly ConcurrentDictionary<Type, IList<PropertyInfo>> CachedTypePropertyInfos =
			new ConcurrentDictionary<Type, IList<PropertyInfo>>();


		//this contract is only used to resolve properties in class WE OWN.
		//these are not subject to change depending on what the user passes as connectionsettings
		private static readonly ElasticContractResolver JsonContract =
			new ElasticContractResolver(new ConnectionSettings(), null);

		internal static object CreateGenericInstance(this Type t, Type closeOver, params object[] args) =>
			t.CreateGenericInstance(new[] { closeOver }, args);

		internal static object CreateGenericInstance(this Type t, Type[] closeOver, params object[] args)
		{
			var key = closeOver.Aggregate(new StringBuilder(t.FullName), (sb, gt) =>
			{
				sb.Append("--");
				return sb.Append(gt.FullName);
			}, sb => sb.ToString());
			if (!CachedGenericClosedTypes.TryGetValue(key, out var closedType))
			{
				closedType = t.MakeGenericType(closeOver);
				CachedGenericClosedTypes.TryAdd(key, closedType);
			}
			return closedType.CreateInstance(args);
		}

		internal static T CreateInstance<T>(this Type t, params object[] args) => (T)t.CreateInstance(args);

		internal static object CreateInstance(this Type t, params object[] args)
		{
			var key = t.FullName;
			var argKey = args.Length;
			if (args.Length > 0)
				key = argKey + "--" + key;
			if (CachedActivators.TryGetValue(key, out var activator))
				return activator(args);

			var generic = GetActivatorMethodInfo.MakeGenericMethod(t);
			var constructors = from c in t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
				let p = c.GetParameters()
				where p.Length == args.Length
				select c;

			var ctor = constructors.FirstOrDefault();
			if (ctor == null)
				throw new Exception($"Cannot create an instance of {t.FullName} because it has no constructor taking {args.Length} arguments");

			activator = (ObjectActivator<object>)generic.Invoke(null, new[] { ctor });
			CachedActivators.TryAdd(key, activator);
			return activator(args);
		}

		//do not remove this is referenced through GetActivatorMethod
		private static ObjectActivator<T> GetActivator<T>(ConstructorInfo ctor)
		{
			var paramsInfo = ctor.GetParameters();

			//create a single param of type object[]
			var param =
				Expression.Parameter(typeof(object[]), "args");

			var argsExp =
				new Expression[paramsInfo.Length];

			//pick each arg from the params array
			//and create a typed expression of them
			for (var i = 0; i < paramsInfo.Length; i++)
			{
				Expression index = Expression.Constant(i);
				var paramType = paramsInfo[i].ParameterType;

				Expression paramAccessorExp =
					Expression.ArrayIndex(param, index);

				Expression paramCastExp =
					Expression.Convert(paramAccessorExp, paramType);

				argsExp[i] = paramCastExp;
			}

			//make a NewExpression that calls the
			//ctor with the args we just created
			var newExp = Expression.New(ctor, argsExp);

			//create a lambda with the New
			//Expression as body and our param object[] as arg
			var lambda =
				Expression.Lambda(typeof(ObjectActivator<T>), newExp, param);

			//compile it
			var compiled = (ObjectActivator<T>)lambda.Compile();
			return compiled;
		}

		internal static IList<JsonProperty> GetCachedObjectProperties(this Type t,
			MemberSerialization memberSerialization = MemberSerialization.OptIn
		)
		{
			IList<JsonProperty> propertyDictionary;
			if (CachedTypeProperties.TryGetValue(t, out propertyDictionary))
				return propertyDictionary;

			propertyDictionary = JsonContract.PropertiesOfAll(t, memberSerialization);
			CachedTypeProperties.TryAdd(t, propertyDictionary);
			return propertyDictionary;
		}

		internal static IList<PropertyInfo> AllPropertiesCached(this Type t)
		{
			IList<PropertyInfo> propertyInfos;
			if (CachedTypePropertyInfos.TryGetValue(t, out propertyInfos))
				return propertyInfos;

			propertyInfos = t.AllPropertiesNotCached().ToList();
			CachedTypePropertyInfos.TryAdd(t, propertyInfos);
			return propertyInfos;
		}

		/// <summary>
		/// Returns inherited properties with reflectedType set to base type
		/// </summary>
		private static IEnumerable<PropertyInfo> AllPropertiesNotCached(this Type type)
		{
			var propertiesByName = new Dictionary<string, PropertyInfo>();
			do
			{
				foreach (var propertyInfo in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
				{
					if (propertiesByName.ContainsKey(propertyInfo.Name))
					{
						if (IsHidingMember(propertyInfo)) propertiesByName[propertyInfo.Name] = propertyInfo;
					}
					else
						propertiesByName.Add(propertyInfo.Name, propertyInfo);
				}
#if DOTNETCORE
				type = type.GetTypeInfo()?.BaseType;
			} while (type?.GetTypeInfo()?.BaseType != null);
#else
				type = type.BaseType;
			} while (type?.BaseType != null);
#endif
			return propertiesByName.Values;
		}

		/// <summary>
		/// Determines if a property is overriding an inherited property of its base class
		/// </summary>
		private static bool IsHidingMember(PropertyInfo propertyInfo)
		{
#if DOTNETCORE
			var baseType = propertyInfo.DeclaringType?.GetTypeInfo()?.BaseType;
#else
			var baseType = propertyInfo.DeclaringType?.BaseType;
#endif
			var baseProperty = baseType?.GetProperty(propertyInfo.Name);
			if (baseProperty == null) return false;

			var derivedGetMethod = propertyInfo.GetGetMethod().GetBaseDefinition();
			return derivedGetMethod?.ReturnType != propertyInfo.PropertyType;
		}
	}
}
