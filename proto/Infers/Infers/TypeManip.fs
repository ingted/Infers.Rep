﻿[<AutoOpen>]
module internal Infers.TypeManip

open System
open System.Reflection

/////////////////////////////////////////////////////////////////////////

type TyCon =
  | Arr of rank: int
  | Def of def: Type

type Ty =
  | Var of Type
  | App of TyCon * array<Ty>

let rec toTy (t: Type) =
  if t.IsArray then
    App (Arr (t.GetArrayRank ()), [|toTy (t.GetElementType ())|])
  elif t.IsGenericParameter then
    Var t
  elif t.IsGenericType then
    App (Def (t.GetGenericTypeDefinition ()),
         t.GetGenericArguments () |> Array.map toTy)
  else
    App (Def t, [||])

let rec ofTy ty =
  match ty with
   | Var t -> t
   | App (Arr r, [|ty|]) -> 
     let t = ofTy ty
     if r = 1
     then t.MakeArrayType ()
     else t.MakeArrayType r
   | App (Arr _, _) -> failwithf "Bug: %A" ty
   | App (Def t, [||]) -> t
   | App (Def t, tys) -> t.MakeGenericType (tys |> Array.map ofTy)

/////////////////////////////////////////////////////////////////////////

let rec containsVars (ty: Ty) =
  match ty with
   | Var v -> true
   | App (_, tys) -> Array.exists containsVars tys

let rec resolveTop v2ty ty =
  match ty with
   | Var v ->
     match HashEqMap.tryFind v v2ty with
      | None -> ty
      | Some ty -> resolveTop v2ty ty
   | _ ->
     ty

let rec resolve v2ty ty =
  match resolveTop v2ty ty with
   | Var v -> Var v
   | App (tc, tys) ->
     App (tc, tys |> Array.map (resolve v2ty))

let rec occurs v2ty v ty =
  match ty with
   | Var v' -> v = v'
   | App (_, tys) ->
     tys
     |> Array.exists (occurs v2ty v << resolveTop v2ty)

/// Given two types, determines whether they can be unified with respect to and
/// by extending the given `v2ty` mapping of type variables.
let rec tryMatch formal actual v2ty =
  match (resolveTop v2ty formal, resolveTop v2ty actual) with
   | (Var fv, Var av) when fv = av ->
     Some v2ty
   | (Var v, ty) | (ty, Var v) when not (occurs v2ty v ty) ->
     Some (HashEqMap.add v ty v2ty)
   | (App (formal, pars), App (actual, args)) when formal = actual ->
     assert (pars.Length = args.Length)
     let rec loop i v2ty =
       if i < pars.Length then
         tryMatch pars.[i] args.[i] v2ty
         |> Option.bind (loop (i+1))
       else
         Some v2ty
     loop 0 v2ty
   | _ ->
     None

/// Given two types, determines whether they are equal, taking into
/// consideration the implicit universal quantification of type variables.
let areEqual aTy bTy =
  let rec types aTy bTy v2ty =
    match (aTy, bTy) with
     | (Var a, Var b) ->
       match HashEqMap.tryFind a v2ty with
        | Some b' ->
          if b' = b then Some v2ty else None
        | None ->
          Some (HashEqMap.add a b v2ty)
     | (App (aTc, aArgs), App (bTc, bArgs)) when aTc = bTc ->
       assert (aArgs.Length = bArgs.Length)
       let rec args i v2ty =
         if aArgs.Length <= i then
           Some v2ty
         else
           match types aArgs.[i] bArgs.[i] v2ty with
            | None -> None
            | Some v2ty ->
              args (i+1) v2ty
       args 0 v2ty
     | _ ->
       None
  types aTy bTy HashEqMap.empty |> Option.isSome

type MoreSpecific =
 | Lhs
 | Rhs
 | Unmatchable
 | Equal
 | Incomparable

/// Given two types, determines whether one of the two types is clearly a more
/// specific type with respect to unification and returns that type if so.
let moreSpecific lhs rhs =
  match tryMatch lhs rhs HashEqMap.empty with
  | None -> Unmatchable
  | Some v2ty ->
    assert (areEqual (resolve v2ty lhs) (resolve v2ty rhs))
    match (areEqual (resolve v2ty lhs) lhs, areEqual (resolve v2ty rhs) rhs) with
     | ( true,  true) -> Equal
     | ( true, false) -> Lhs
     | (false,  true) -> Rhs
     | (false, false) -> Incomparable

let inPlaceSelectSpecificFirst (tyrs: array<Ty * 'r>) =
  let swap i j =
    let iEl = tyrs.[i]
    tyrs.[i] <- tyrs.[j]
    tyrs.[j] <- iEl
  for i=0 to tyrs.Length-2 do
    let mutable j = i
    for k=i+1 to tyrs.Length-1 do
      if Rhs = moreSpecific (fst tyrs.[j]) (fst tyrs.[k]) then
        j <- k
    swap i j

/////////////////////////////////////////////////////////////////////////

type TyCursor = list<int>

let rec getAt is ty =
  match is with
   | [] ->
     Some ty
   | i::is ->
     match getAt is ty with
      | Some (App (_, args)) when i < args.Length ->
        Some args.[i]
      | _ ->
        None

type TyTreeFun<'r> =
  | Empty
  | One of 'r
  | Many of array<'r>
  | Branch of at: TyCursor *
              apps: HashEqMap<TyCon, TyTree<'r>> *
              vars: TyTree<'r>
and TyTree<'r> = Lazy<TyTreeFun<'r>>

module TyTree =
  let rec build' (ats: list<TyCursor>) (tyrs: list<Ty * 'r>) : TyTree<'r> =
    lazy match tyrs with
          | [] -> Empty
          | [(_, r)] -> One r
          | tyrs ->
            match ats with
             | [] ->
               tyrs
               |> Array.ofList
               &> inPlaceSelectSpecificFirst
               |> Array.map snd
               |> Many
             | at::ats ->
               tyrs
               |> List.fold
                   (fun (apps, vars) (ty, r) ->
                     match getAt at ty with
                      | None -> failwith "Bug"
                      | Some (Var _) ->
                        (apps, (ty, r) :: vars)
                      | Some (App (tc, args)) ->
                        (HashEqMap.addOrUpdate tc
                          (fun _ -> (args.Length, [(ty, r)]))
                          (fun _ (n, tyrs) -> (n, (ty, r)::tyrs))
                          apps,
                         vars))
                   (HashEqMap.empty, [])
               |> fun (apps, vars) ->
                  let vars = build' ats vars
                  if HashEqMap.isEmpty apps then
                    force vars
                  else
                    (at,
                     apps
                     |> HashEqMap.map
                         (fun (n, tyds) ->
                            build'
                             (List.init n (fun i -> i::at) @ ats)
                             tyds),
                     vars) |> Branch

  let build tyrs = build' [[]] (List.ofSeq tyrs)

  let rec filter (formal: TyTree<'r>) (actual: Ty) : seq<'r> =
    match force formal with
     | Empty -> Seq.empty
     | One r -> Seq.singleton r
     | Many rs -> rs :> seq<_>
     | Branch (at, apps, vars) ->
       match getAt at actual with
        | None | Some (Var _) -> seq {
            yield! HashEqMap.toSeq apps
                   |> Seq.collect (fun (_, formal) ->
                      filter formal actual)
            yield! filter vars actual
          }
        | Some (App (tc, args)) ->
          match HashEqMap.tryFind tc apps with
           | None ->
             filter vars actual
           | Some formal ->
             filter formal actual

/////////////////////////////////////////////////////////////////////////

let prepare (m: MethodInfo) (v2ty: HashEqMap<Type, Ty>) : MethodInfo =
  if m.ContainsGenericParameters then
    m.MakeGenericMethod
     (m.GetGenericArguments ()
      |> Array.map (toTy >> resolve v2ty >> ofTy))
  else
    m

let tryInvoke meth formalType actualType this actuals =
  tryMatch formalType actualType HashEqMap.empty
  |> Option.map (fun v2ty ->
     (prepare meth v2ty).Invoke (this, actuals))
