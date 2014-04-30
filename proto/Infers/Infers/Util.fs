﻿[<AutoOpen>]
module internal Infers.Util

open System

/////////////////////////////////////////////////////////////////////////

let inline toFun (f: Func<'a, 'b, 'c>) x y = f.Invoke (x, y)
let inline toFunc (f: 'a -> 'b -> 'c) = Func<'a, 'b, 'c>(f)

/////////////////////////////////////////////////////////////////////////

let inline (|GotSome|GotNone|) (b, v) =
  if b then GotSome v else GotNone

/////////////////////////////////////////////////////////////////////////

let inline id x = x

/////////////////////////////////////////////////////////////////////////

let notImplemented () = raise (NotImplementedException ())

/////////////////////////////////////////////////////////////////////////

module Option =
  let toSeq o = match o with None -> Seq.empty | Some x -> Seq.singleton x

/////////////////////////////////////////////////////////////////////////

module HashEqSet =
  type t<'k> when 'k: equality = T of Map<int, list<'k>>

  let empty : t<'k> = T Map.empty

  let drop k ks =
    if List.exists ((=) k) ks then
      List.filter ((<>) k) ks
    else
      ks

  let add (k: 'k) (T s: t<'k>) =
    let hk = hash k
    Map.add hk
     (match Map.tryFind hk s with
       | None -> [k]
       | Some ks -> k :: drop k ks)
     s |> T

  let contains (k: 'k) (T s: t<'k>) =
    match Map.tryFind (hash k) s with
     | None -> false
     | Some ks -> List.exists ((=) k) ks

  let toSeq (T s) = Map.toSeq s |> Seq.collect snd

type HashEqSet<'k> when 'k: equality = HashEqSet.t<'k>

/////////////////////////////////////////////////////////////////////////

module HashEqMap =
  type t<'k, 'v> when 'k: equality = T of Map<int, list<'k * 'v>>

  let drop (k: 'k) (kvs: list<'k * 'v>) =
    if List.exists (fst >> (=) k) kvs then
      List.filter (fst >> (<>) k) kvs
    else
      kvs

  let empty : t<_, _> = T Map.empty

  let add (k: 'k) (v: 'v) (T m: t<'k, 'v>) =
    let hk = hash k
    Map.add hk
     ((k, v)
       :: match Map.tryFind hk m with
           | None -> []
           | Some kvs -> drop k kvs)
     m |> T

  let tryFind (k: 'k) (T m: t<'k, 'v>) : option<'v> =
    Map.tryFind (hash k) m
    |> Option.bind (fun kvs ->
       List.tryFind (fst >> (=) k) kvs
       |> Option.bind (fun (_, v) ->
          Some v))

  let toSeq (T m) = Map.toSeq m |> Seq.collect snd

type HashEqMap<'k, 'v> when 'k: equality = HashEqMap.t<'k, 'v>
