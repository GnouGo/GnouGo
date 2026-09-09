namespace GnOuGo.Flow.Authoring.JavaScript;

internal static class JavaScriptSdk
{
    internal const string Documentation = """
        JavaScript authoring SDK v1. End with flow.workflow({...}); no imports or exports.
        JavaScript runs only during construction. Runtime branching/loops must be native nodes, not JavaScript if/for on symbolic references.
        Template keys, operationIds, capabilityId and purpose are copied automatically by step key; preserve every reviewed node and container.
        /** @typedef {Object} Value A typed symbolic runtime value; never inspect/coerce it during authoring. */
        /** @typedef {Object} Schema Planning schema: {type, nullable?, properties?:[{name,schema,required}], items?};
         * or {capabilityId, schemaPointer} referencing an authoritative JSON schema. */
        /** @returns {Value} */ flow.input(name, ...path);
        /** @returns {Value} */ flow.ref(nodeKey, ...path); // ordinary result
        /** @returns {Value} */ flow.structured(nodeKey, ...path); // validated structured result
        /** @returns {Value} */ flow.envelope(nodeKey, ...path); // full MCP result envelope
        /** @returns {Value} */ flow.item(loopKey, ...path);
        /** @returns {Value} */ flow.index(loopKey);
        /** @returns {Value} */ flow.previous(loopKey, ...path);
        /** @returns {Value} */ flow.artifactCollection(loopKey, childKey, ...path); // unchanged producer records
        /** @returns {Value} */ flow.value(typedValue); // advanced PlanningValue contract, independently validated
        /** @returns {Value} */ flow.expr(text); // runtime JS with data.inputs/data.steps, validated separately
        /** @returns {Value} */ flow.compute(bindings, body); // explicit symbolic parameters and runtime body
        /** @returns {Value} */ flow.literal(value); // force a literal, including objects with a kind property
        flow.port(name, schema, required = true, defaultValue = undefined);
        flow.result(name, schema, value);
        flow.step(key, type, input = {}, options = {});
        flow.call(key, workflowKey, args = {}, options = {}); // native workflow.call; args are the callee's inputs
        flow.when(key, condition, steps, options = {}); // native sequence with an if guard
        flow.loop(key, items, steps, options = {}); // native loop.sequential; use loop.parallel via step if declared
        flow.parallel(key, branches, options = {}); // branches is an array of step arrays
        flow.choose(key, expression, cases, defaultSteps = [], options = {}); // cases: {value?,when?,steps}
        flow.workflow({inputs:[flow.port(...)], outputs:[flow.result(...)], steps:[...], finally:[...], functions:"..."});
        Options use native typed graph fields: if, expr, outputSchema, structuredOutput:{schema,strict}, output, itemVar, indexVar,
        retry, onError:[{if?,action,setOutput?,retry?}], steps, branches, cases, default. Use SDK values for executable references.
        Inputs, output values, guards and error fallback values are recursively converted; ordinary JS values become literals.
        Every runtime helper in functions requires JSDoc @param/@returns and explicit parameters; no authoring closures.
        Example: flow.workflow({inputs:[], steps:[flow.step("greeting", "set", {message:"ready"},
          {outputSchema:{type:"object",properties:[{name:"message",schema:{type:"string"},required:true}]}})],
          outputs:[flow.result("message",{type:"string"},flow.ref("greeting","message"))], finally:[]});
        """;

    internal const string Runtime = """
        'use strict';
        const flow = (() => {
          const marked = new WeakSet();
          const tag = value => { marked.add(value); return Object.freeze(value); };
          const value = v => {
            if (v !== null && typeof v === 'object' && marked.has(v)) return v;
            if (v === null) return {kind:'null'};
            if (typeof v === 'string') return {kind:'string',text:v};
            if (typeof v === 'boolean') return {kind:'boolean',boolean:v};
            if (typeof v === 'number' && Number.isFinite(v)) return {kind:'number',number:v};
            if (Array.isArray(v)) return {kind:'array',items:v.map(value)};
            if (typeof v === 'object') return {kind:'object',members:Object.keys(v).map(name=>({name,value:value(v[name])}))};
            throw new Error('Only JSON values or SDK references may enter the workflow.');
          };
          let template;
          const nodes = new Map();
          const index = steps => { for (const n of steps || []) {
            nodes.set(n.key,n); index(n.steps); index(n.default);
            for(const b of n.branches || []) index(b.steps);
            for(const c of n.cases || []) index(c.steps);
          }};
          const step = (key,type,input={},options={}) => {
            const original = nodes.get(key) || {};
            const n = {key,type,purpose:original.purpose || '',operationIds:original.operationIds || [],
              capabilityId:original.capabilityId || null,...options,input:value(input)};
            for(const p of ['if','expr']) if(n[p] !== undefined && n[p] !== null) n[p]=value(n[p]);
            if(n.onError) n.onError=n.onError.map(e=>({...e,
              if:e.if == null ? null : value(e.if),setOutput:e.setOutput === undefined ? null : value(e.setOutput)}));
            return n;
          };
          return Object.freeze({
            initialize:t=>{if(template) throw new Error('Already initialized');template=t;index(t.steps);index(t.finally);},
            literal:v=>tag(value(v)),
            input:(source,...path)=>tag({kind:'input',source,path}),
            ref:(source,...path)=>tag({kind:'output',source,path}),
            structured:(source,...path)=>tag({kind:'output',source,path,resultChannel:'structured'}),
            envelope:(source,...path)=>tag({kind:'output',source,path,resultChannel:'envelope'}),
            item:(source,...path)=>tag({kind:'loop_item',source,path}),
            index:source=>tag({kind:'loop_index',source,path:[]}),
            previous:(source,...path)=>tag({kind:'loop_previous',source,path}),
            artifactCollection:(source,...path)=>tag({kind:'artifact_collection',source,path}),
            value:v=>tag(v),
            expr:text=>tag({kind:'expression',text}),
            compute:(bindings,text)=>tag({kind:'compute',text,members:Object.keys(bindings).map(name=>({name,value:value(bindings[name])}))}),
            port:(name,schema,required=true,defaultValue=undefined)=>({name,schema,required,...(defaultValue===undefined?{}:{default:value(defaultValue)})}),
            result:(name,schema,v)=>({name,schema,value:value(v)}),
            step,
            call:(key,ref,args={},options={})=>step(key,'workflow.call',{args,ref:tag({kind:'workflow',source:ref})},options),
            when:(key,condition,steps,options={})=>step(key,'sequence',{}, {...options,if:condition,steps}),
            loop:(key,items,steps,options={})=>step(key,'loop.sequential',{items},{...options,steps}),
            parallel:(key,branches,options={})=>step(key,'parallel',{}, {...options,branches:branches.map(steps=>({steps}))}),
            choose:(key,expression,cases,defaultSteps=[],options={})=>step(key,'switch',{}, {...options,expr:expression,
              cases:cases.map(c=>({...c,when:c.when == null ? null : value(c.when)})),default:defaultSteps}),
            workflow:options=>({key:template.key,purpose:template.purpose,operationIds:template.operationIds,
              inputs:[],outputs:[],steps:[],finally:[],...options})
          });
        })();
        Object.defineProperty(Math, 'random', {value:undefined,writable:false,configurable:false});
        Object.defineProperty(globalThis, 'Date', {value:undefined,writable:false,configurable:false});
        Object.defineProperty(globalThis, 'Intl', {value:undefined,writable:false,configurable:false});
        """;
}
