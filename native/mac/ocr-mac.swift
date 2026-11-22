// ocr-mac.swift —— 给 Lens 的 macOS 引擎用的 OCR 小工具。
//
// 为什么需要它：macOS 的文字识别在 Vision.framework 里，只能从 Swift/ObjC 调，
// JXA 封不住。这个程序只有一百多行，用系统自带的 swiftc 就能编译，不引第三方库。
//
// 用法：
//   swiftc -O ocr-mac.swift -o ocr-mac
//   ./ocr-mac /path/to.png [--lang zh-Hans,en-US]
//
// 输出（stdout，单行 JSON）：
//   {"ok":true,"words":[{"text":"发送","x":120,"y":340,"w":48,"h":22,"conf":0.96}, ...]}
// 坐标是**图片像素、左上角原点**，与 Lens 其它通道一致（Vision 自己给的是归一化 + 左下角原点）。

import Foundation
import Vision
import AppKit

func fail(_ code: String, _ message: String) -> Never {
    let payload: [String: Any] = ["ok": false, "code": code, "message": message]
    if let data = try? JSONSerialization.data(withJSONObject: payload, options: []),
       let text = String(data: data, encoding: .utf8) {
        print(text)
    } else {
        print("{\"ok\":false,\"code\":\"\(code)\"}")
    }
    exit(1)
}

let args = CommandLine.arguments
guard args.count >= 2 else { fail("BAD_ARGS", "usage: ocr-mac <image.png> [--lang zh-Hans,en-US]") }
let path = args[1]

var languages = ["zh-Hans", "en-US"]
if let i = args.firstIndex(of: "--lang"), i + 1 < args.count {
    languages = args[i + 1].split(separator: ",").map { String($0) }
}

guard let image = NSImage(contentsOfFile: path) else { fail("IMAGE_NOT_FOUND", path) }
guard let tiff = image.tiffRepresentation,
      let rep = NSBitmapImageRep(data: tiff),
      let cgImage = rep.cgImage else { fail("IMAGE_DECODE_FAILED", path) }

let pixelWidth = CGFloat(cgImage.width)
let pixelHeight = CGFloat(cgImage.height)

let request = VNRecognizeTextRequest()
request.recognitionLevel = .accurate
request.usesLanguageCorrection = true
request.recognitionLanguages = languages

let handler = VNImageRequestHandler(cgImage: cgImage, options: [:])
do {
    try handler.perform([request])
} catch {
    fail("OCR_FAILED", "\(error)")
}

var words: [[String: Any]] = []
if let observations = request.results {
    for obs in observations {
        guard let top = obs.topCandidates(1).first else { continue }
        // Vision 的 boundingBox：归一化、左下角原点 → 转成像素、左上角原点
        let bb = obs.boundingBox
        let x = bb.origin.x * pixelWidth
        let w = bb.size.width * pixelWidth
        let h = bb.size.height * pixelHeight
        let y = (1.0 - bb.origin.y - bb.size.height) * pixelHeight
        words.append([
            "text": top.string,
            "x": Int(x.rounded()),
            "y": Int(y.rounded()),
            "w": Int(w.rounded()),
            "h": Int(h.rounded()),
            "conf": Double(String(format: "%.4f", top.confidence)) ?? 0,
        ])
    }
}

let payload: [String: Any] = [
    "ok": true,
    "words": words,
    "imageWidth": Int(pixelWidth),
    "imageHeight": Int(pixelHeight),
    "languages": languages,
]
guard let data = try? JSONSerialization.data(withJSONObject: payload, options: []),
      let text = String(data: data, encoding: .utf8) else {
    fail("ENCODE_FAILED", "json")
}
print(text)
