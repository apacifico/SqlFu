using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Dynamic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;

namespace SqlFu
{
    public static class PocoFactory
    {
      
        public static void RegisterConverterFor<T>(Func<object,T> conv)
        {
            conv.MustNotBeNull();
            _converters.TryAdd(typeof (T).GetHashCode(), conv);
        }

        static ConcurrentDictionary<int,object> _converters= new ConcurrentDictionary<int, object>();
        internal static Func<object,T> GetConverter<T>()
        {
            var key = typeof (T).GetHashCode();
            object rez;
            if (_converters.TryGetValue(key,out rez))
            {
                return (Func<object, T>) rez;
            }

            return o =>
                       {
                           if (o==DBNull.Value)
                           {
                               var tp = typeof (T);
                               if (tp.IsValueType && !tp.IsNullable())
                               {
                                   throw new InvalidCastException("Can't convert db null to value type");
                               }
                               return default(T);
                           }
                           return o.ConvertTo<T>();
                       };
        }

        public static IMapComplexType ComplexTypeMapper= new DefaultCompexTypeMapper();


       #region Poco Mapping
		
        public static void RegisterMapperFor<T>(Func<IDataReader,T> mapper)
        {
            _customMappers.TryAdd(typeof (T), mapper);
        }


        static ConcurrentDictionary<Type, object> _customMappers = new ConcurrentDictionary<Type, object>();
        static ConcurrentDictionary<Identity,object> _pocoCache= new ConcurrentDictionary<Identity,object>();

        static Func<IDataReader, dynamic> _dynamicPoco = rd =>
        {
            var d = new ExpandoObject();
            var o = d as IDictionary<string, object>;
            for (int i = 0; i < rd.FieldCount; i++)
            {
                o[rd.GetName(i)] = rd.GetValue(i);
            }
            return d;
        };


        internal static Func<IDataReader,T> GetPocoMapper<T>(IDataReader rd,string sql)
        {
            var poco = typeof (T);
            
            //dynamic
             if (poco==typeof(object) || poco==typeof(ExpandoObject))
             {

                 return (Func<IDataReader, T>)(object)_dynamicPoco;
             }
             
            //try custom mappers
             object rez;
            if (_customMappers.TryGetValue(poco,out rez))
            {
                return (Func<IDataReader, T>)rez;
            }
            
             var key = new Identity(poco,rd,sql);
           //  Console.WriteLine("key {0} {1}",key,key.GetHashCode());
            //get from cache
            if (!_pocoCache.TryGetValue(key,out rez))
            {
                    //emit mapper    
                    var meth = new DynamicMethod("poco_map" + key.GetHashCode(), poco, new[] { typeof(IDataReader) },
                                                 typeof(PocoFactory),true);
                    var il = meth.GetILGenerator();
                
                    
                    if (poco.IsCustomObjectType())
                    {
                        EmitPocoMapper(il, poco, rd);   
                        
                    }
                    else
                    {
                        EmitValueMapper(il, poco);
                    }

                rez = meth.CreateDelegate(typeof(Func<IDataReader, T>));                 
                
                _pocoCache.TryAdd(key, rez);
            }
            return (Func<IDataReader, T>) rez;
        }


        internal static FieldInfo _ComplexMapperInfo = (typeof (PocoFactory)).GetField("ComplexTypeMapper",
                                                                                      BindingFlags.GetField |
                                                                                      BindingFlags.Static |
                                                                                      BindingFlags.Public);
        private static void EmitPocoMapper(ILGenerator il, Type poco,IDataReader rd)
        {
            il.DeclareLocal(poco);
            ComplexTypeMapper.DeclareILVariables(il);
            var c = poco.GetConstructor(BindingFlags.CreateInstance | BindingFlags.NonPublic | BindingFlags.Public, null,
                                        Type.EmptyTypes, null);
            il.Emit(OpCodes.Newobj,poco.GetConstructor(Type.EmptyTypes));            
            il.Emit(OpCodes.Stloc_0);//saved at loc 0
           
           
            var allp = poco.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.SetProperty);
            string name;
            for(int i=0;i<rd.FieldCount;i++)
            {
                name = rd.GetName(i);
                if (ComplexTypeMapper.IsComplex(name))
                {
                    if (!ComplexTypeMapper.EmitMapping(il, poco, rd, i))
                    {
                        il.Emit(OpCodes.Ldsfld, _ComplexMapperInfo);
                        il.Emit(OpCodes.Ldloc_0); //poco
                        il.Emit(OpCodes.Ldarg_0); //rd
                        il.Emit(OpCodes.Ldc_I4, i);
                        il.Emit(OpCodes.Callvirt, typeof(IMapComplexType).GetMethod("MapType").MakeGenericMethod(poco));//call MapType
                    }
                    continue;
                }
                
                var prop = allp.FirstOrDefault(d => d.Name.ToUpperInvariant() == name.ToUpperInvariant());
                if (prop==null) continue;
                if (prop.PropertyType.IsCustomObjectType()) continue;
                
                    //throw new InvalidCastException(string.Format("Can't convert {0} to {1} ",rd.GetFieldType(i).ToString(),prop.PropertyType.ToString()));
                il.Emit(OpCodes.Ldloc_0);//poco
                EmitGetColumnValue(il,i,prop.PropertyType);//we have a value here
                il.Emit(OpCodes.Callvirt,prop.GetSetMethod());//poco.property.setvalue
            }
            il.Emit(OpCodes.Ldloc_0);//poco
            il.Emit(OpCodes.Ret);
        }

        /// <summary>
        /// Read value and puts it on the stack
        /// </summary>
        /// <param name="il"></param>
        /// <param name="i"></param>
        /// <param name="tp"></param>
        public static void EmitGetColumnValue(ILGenerator il,int i,Type tp)
        {
            
            
           il.Emit(OpCodes.Ldarg_0);//rd
            il.Emit(OpCodes.Ldc_I4,i);//i
            il.Emit(OpCodes.Callvirt, _rdIsDbNullInfo);//rd.isdbnull(i)
    
            var end = il.DefineLabel();
            var endIf = il.DefineLabel();
    il.Emit(OpCodes.Brfalse_S, endIf);//if not null jump
            
            if (tp.IsNullable())
            {
                LocalBuilder rez;
                il.EmitNewEmptyNullable(tp, out rez);
                il.Emit(OpCodes.Ldloc, rez);
                
            }
            
            else {
                if (tp.IsValueType)
                {
                    il.EmitThrowException<InvalidCastException>();
                }
            }
            if (tp.IsClass)
            {
                il.Emit(OpCodes.Ldnull);
                
            }
         il.Emit(OpCodes.Br, end);
    il.MarkLabel(endIf);

            var getter = typeof (IDataRecord).GetMethod("Get" + tp.Name, new Type[] {typeof (int)});
            if (getter != null)
            {
                il.EmitLoadMethodArgument(0);
                il.Emit(OpCodes.Ldc_I4, i);//i
                il.Emit(OpCodes.Callvirt, getter);

            }

            else
            {
                //get converter delegate
                il.Emit(OpCodes.Call,
                        (typeof (PocoFactory).GetMethod("GetConverter", BindingFlags.Static | BindingFlags.NonPublic)).
                            MakeGenericMethod(tp));

                //get reader value
                il.Emit(OpCodes.Ldarg_0); //rd
                il.Emit(OpCodes.Ldc_I4, i); //i
                il.Emit(OpCodes.Call, _rdGetValueInfo); //poco,rd.GetValue(i)

                //invoke delegate 
                il.Emit(OpCodes.Call, (Expression.GetFuncType(typeof (object), tp)).GetMethod("Invoke"));
            }
            il.MarkLabel(end);//stack contains converted value
        }

        //private static readonly MethodInfo _converterInfo = typeof (ObjectExtend).GetMethod("ConvertTo",
        //                                                                          new[]{typeof(object),typeof(Type)});

        private static readonly MethodInfo _rdGetValueInfo = typeof (IDataRecord).GetMethod("GetValue", new[] {typeof (Int32)});
        private static readonly MethodInfo _rdIsDbNullInfo = typeof (IDataRecord).GetMethod("IsDBNull", new[] {typeof (Int32)});

       
        static void EmitValueMapper(ILGenerator il,Type poco)
        {
           EmitGetColumnValue(il,0,poco);
        il.Emit(OpCodes.Ret);
        }

     

	#endregion

       

      //  #region Parameter Mapping
        //public delegate string[] ParamMapperDelegate(IDbCommand cmd, IHaveDbProviderConfig provider, object poco);
 
        
        //private static ConcurrentDictionary<int, ParamMapperDelegate> _paramCache =
        // new ConcurrentDictionary<int, ParamMapperDelegate>();
    
        //static ParamMapperDelegate _expandoMapper = (c, p, dt) =>
        //                                                 {
        //                                                     var d = dt as IDictionary<string, object>;
        //                                                     var allp = c.Parameters;
        //                                                     var i = 0;
        //                                                     var pn = new string[d.Keys.Count];
        //                                                     foreach (var key in d.Keys)
        //                                                     {
        //                                                         var par = c.CreateParameter();
        //                                                         p.SetupParameter(par, key, d[key]);
        //                                                         allp.Add(par);
        //                                                         pn[i] = key;
        //                                                         i++;
        //                                                     }
        //                                                     return pn;
        //                                                 };

        //internal static PropertyInfo DbCmdParametersProperty = typeof(IDbCommand).GetProperty("Parameters");



        //public static ParamMapperDelegate GetParametersMapper(Type poco)
        //{
        //    var key = poco.GetHashCode();
        //    ParamMapperDelegate rez = null;
        //    if (poco == typeof(ExpandoObject))
        //    {
        //        return _expandoMapper;
        //    }
        //    if (!_paramCache.TryGetValue(key, out rez))
        //    {
        //        var meth = new DynamicMethod("pm" + key, typeof(string[]),
        //                                     new[] { typeof(IDbCommand), typeof(IHaveDbProviderConfig), typeof(object) },
        //                                     typeof(PocoFactory).Module);
        //        var il = meth.GetILGenerator();
        //        var allp = poco.GetProperties(BindingFlags.Instance | BindingFlags.GetProperty | BindingFlags.Public);
        //        var len = allp.Length;
        //        il.DeclareLocal(typeof(IDataParameterCollection));
        //        il.DeclareLocal(typeof(IDbDataParameter));
        //        il.DeclareLocal(typeof(string[]));
        //        il.Emit(OpCodes.Ldc_I4, len);
        //        il.Emit(OpCodes.Newarr, typeof(string));
        //        il.Emit(OpCodes.Stloc_2); //save new array
        //        il.Emit(OpCodes.Ldarg_0);
        //        il.Emit(OpCodes.Callvirt, DbCmdParametersProperty.GetGetMethod());//allparams
        //        il.Emit(OpCodes.Stloc_0); //all params saved to l0
        //        for (int i = 0; i < len; i++)
        //        {
        //            var prop = allp[i];
        //            var anme = prop.Name;
        //            il.Emit(OpCodes.Ldloc_2); //load array
        //            il.Emit(OpCodes.Ldc_I4, i);// i
        //            il.Emit(OpCodes.Ldstr, anme);//prop name
        //            il.Emit(OpCodes.Stelem_Ref);//arr[i]=name
        //            il.Emit(OpCodes.Ldarg_0);//load cmd
        //            il.Emit(OpCodes.Callvirt, typeof(IDbCommand).GetMethod("CreateParameter"));//par 
        //            il.Emit(OpCodes.Stloc_1);//par saved to l1
        //            il.Emit(OpCodes.Ldarg_1);//provider
        //            il.Emit(OpCodes.Ldloc_1);//provider, par
        //            il.Emit(OpCodes.Ldstr, anme);// provider,par,name
        //            il.Emit(OpCodes.Ldarg_2);//poco
        //            il.Emit(OpCodes.Call, prop.GetGetMethod());//poco,getter
        //            if (prop.PropertyType.IsValueType)
        //            {
        //                il.Emit(OpCodes.Box, prop.PropertyType);
        //            }
        //            //provider,par,name,value
        //            il.Emit(OpCodes.Callvirt, typeof(IHaveDbProviderConfig).GetMethod("SetupParameter"));
        //            il.Emit(OpCodes.Ldloc_0);//all params
        //            il.Emit(OpCodes.Ldloc_1);//all params, par
        //            il.Emit(OpCodes.Callvirt, typeof(IList).GetMethod("Add"));

        //        }
        //        il.Emit(OpCodes.Ldloc_2);
        //        il.Emit(OpCodes.Ret);
        //        rez = (ParamMapperDelegate)meth.CreateDelegate(typeof(ParamMapperDelegate));
        //        _paramCache.TryAdd(key, rez);
        //    }
        //    return rez;
        //}

        //    static ConcurrentDictionary<Signature,ParamArrayMapperDelegate> _paramArrayCache= new ConcurrentDictionary<Signature,ParamArrayMapperDelegate>();
        //      public delegate string[] ParamArrayMapperDelegate(IDbCommand cmd, IHaveDbProviderConfig provider, object[] args);
        //public static ParamArrayMapperDelegate GetParametersArrayMapper(object[] args)
        //{
        //    var key = new Signature(args);
        //    ParamArrayMapperDelegate rez = null;

        //    if (!_paramArrayCache.TryGetValue(key, out rez))
        //    {

        //        var meth = new DynamicMethod("pm" + key, typeof(string[]),
        //                                     new[]{typeof(IDbCommand), typeof(IHaveDbProviderConfig),typeof(object[])},
        //                                     typeof(PocoFactory).Module);
        //        var il = meth.GetILGenerator();

        //        var len = args.Length;
        //        il.DeclareLocal(typeof(IDataParameterCollection));
        //        il.DeclareLocal(typeof(IDbDataParameter));
        //        il.DeclareLocal(typeof(string[]));
        //        il.Emit(OpCodes.Ldc_I4, len);
        //        il.Emit(OpCodes.Newarr, typeof(string));
        //        il.Emit(OpCodes.Stloc_2); //save new array
        //        il.Emit(OpCodes.Ldarg_0);
        //        il.Emit(OpCodes.Callvirt, DbCmdParametersProperty.GetGetMethod());//allparams
        //        il.Emit(OpCodes.Stloc_0); //all params saved to l0
        //        for (int i = 0; i < len; i++)
        //        {
        //            var anme = i.ToString();
        //            il.Emit(OpCodes.Ldloc_2); //load array
        //            il.Emit(OpCodes.Ldc_I4, i);// i
        //            il.Emit(OpCodes.Ldstr, anme);//prop name
        //            il.Emit(OpCodes.Stelem_Ref);//arr[i]=name
        //            il.Emit(OpCodes.Ldarg_0);//load cmd
        //            il.Emit(OpCodes.Callvirt, typeof(IDbCommand).GetMethod("CreateParameter"));//par 
        //            il.Emit(OpCodes.Stloc_1);//par saved to l1
        //            il.Emit(OpCodes.Ldarg_1);//provider
        //            il.Emit(OpCodes.Ldloc_1);//provider, par
        //            il.Emit(OpCodes.Ldstr, anme);// provider,par,name

        //            il.Emit(OpCodes.Ldarg, 2);//args[]
        //            il.Emit(OpCodes.Ldc_I4, i);// i
        //            il.Emit(OpCodes.Ldelem,typeof(object));//args[i]

        //            //provider,par,name,value
        //            il.Emit(OpCodes.Callvirt, typeof(IHaveDbProviderConfig).GetMethod("SetupParameter"));
        //            il.Emit(OpCodes.Ldloc_0);//all params
        //            il.Emit(OpCodes.Ldloc_1);//all params, par
        //            il.Emit(OpCodes.Callvirt, typeof(IList).GetMethod("Add"));

        //        }
        //        il.Emit(OpCodes.Ldloc_2);
        //        il.Emit(OpCodes.Ret);
        //        rez = (ParamArrayMapperDelegate)meth.CreateDelegate(typeof(ParamArrayMapperDelegate));
        //        _paramArrayCache.TryAdd(key, rez);
        //    }
        //    return rez;
        //}
      //  #endregion
    }
}