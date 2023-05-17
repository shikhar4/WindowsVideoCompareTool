#include "pch.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <iostream>
#include <fstream>
#include <regex>
#include <filesystem>
#include <chrono>
#include <algorithm>

// generated 
#include <winrt\Windows.Storage.h>

extern "C" {
#include "libavcodec/avcodec.h"
#include "libavformat/avformat.h"
#include "libavutil/motion_vector.h"
}

using namespace std;
using namespace winrt;
using namespace winrt::Windows::Storage;


static AVFormatContext* fmt_ctx = NULL;
static AVCodecContext* video_dec_ctx = NULL;
static AVStream* video_stream = NULL;
const char* src_filename = NULL;

static std::string dst_filename;
static std::string dst_filename_complete;
static ofstream ofile;
static ofstream ofileComplete;

std::string error = "";

static int video_stream_idx = -1;
static AVFrame* frame = NULL;
static int video_frame_count = 0;
const int maxframes = 1000000;

static int open_codec_context(AVFormatContext* fmt_ctx, enum AVMediaType type, float* unit, float* framerate)
{
	int ret;
	AVStream* st;
	AVCodecContext* dec_ctx = NULL;
	AVCodec* dec = NULL;
	AVDictionary* opts = NULL;

	ret = av_find_best_stream(fmt_ctx, type, -1, -1, &dec, 0);
	if (ret < 0) {
		fprintf(stderr, "Could not find %s stream in input file '%s'\n",
			av_get_media_type_string(type), src_filename);
		return ret;
	}
	else {
		int stream_idx = ret;
		st = fmt_ctx->streams[stream_idx];

		dec_ctx = avcodec_alloc_context3(dec);
		if (!dec_ctx) {
			fprintf(stderr, "Failed to allocate codec\n");
			return AVERROR(EINVAL);
		}

		ret = avcodec_parameters_to_context(dec_ctx, st->codecpar);
		if (ret < 0) {
			fprintf(stderr, "Failed to copy codec parameters to codec context\n");
			return ret;
		}

		/* Init the video decoder */
		av_dict_set(&opts, "flags2", "+export_mvs", 0);
		if ((ret = avcodec_open2(dec_ctx, dec, &opts)) < 0) {
			fprintf(stderr, "Failed to open %s codec\n",
				av_get_media_type_string(type));
			return ret;
		}

		video_stream_idx = stream_idx;
		video_stream = fmt_ctx->streams[video_stream_idx];
		video_dec_ctx = dec_ctx;
		*unit = av_q2d(st->time_base);
		*framerate = av_q2d(st->avg_frame_rate);
	}
	return 0;

}

int main(int argc, char** argv)
{
	int ret = 0;
	AVPacket pkt = { 0 };
	float unit = 0.0;
	float framerate = 25.0;
	int totalframes = 0;
	int64_t* pts_array;
	pts_array = (int64_t*)malloc(maxframes * sizeof(int64_t));
	int64_t ppts;

	ofstream successFile;
	std::string successFileName;

	std::string vidFilePathTempStr = "";
	std::string folderPathStr = "";

	// Retrieve the parameters that were set by the UWP app (the video file and PTS text file paths)
	auto appDataValues = Windows::Storage::ApplicationData::Current().LocalSettings().Values();

	if (appDataValues.HasKey(L"vidFilePath") && appDataValues.HasKey(L"ptsFilePath") && appDataValues.HasKey(L"cacheFolderPath"))
	{
		winrt::hstring vidFilePath = winrt::unbox_value<winrt::hstring>(appDataValues.Lookup(L"vidFilePath"));
		std::string vidFilePathStr = winrt::to_string(vidFilePath);

		winrt::hstring ptsFilePath = winrt::unbox_value<winrt::hstring>(appDataValues.Lookup(L"ptsFilePath"));
		std::string ptsFilePathStr = winrt::to_string(ptsFilePath);

		winrt::hstring folderPath = winrt::unbox_value<winrt::hstring>(appDataValues.Lookup(L"cacheFolderPath"));
		folderPathStr = winrt::to_string(folderPath);

		// parse for src_filename 
		for (int i = 0; i < vidFilePathStr.length(); ++i)
		{
			vidFilePathTempStr += vidFilePathStr[i];
		}

		src_filename = vidFilePathTempStr.c_str();
		dst_filename = ptsFilePathStr;
	}
	else // Parameters not found 
	{
		error = "Parameters not found";
		//appDataValues.Insert(L"errorMsg", winrt::box_value(L"Parameters not found"));
		ret = 1;
		goto end;
	}

	//auto numParam = appDataValues.Size();
	//cout << "NUMBER OF PARAMS: " << numParam << "\n\n";

	/* Debugging purposes */
	cout << "\nsource file path: " << src_filename;
	cout << "\npts file path: " << dst_filename;
	cout << "\nfolder path: " << folderPathStr << "\n";

	if (avformat_open_input(&fmt_ctx, src_filename, NULL, NULL) < 0) {
		/*fprintf(stderr, "Could not open source file %s\n", src_filename);
		exit(1);*/
		error = "Could not open source file " + vidFilePathTempStr;
		ret = 1;
		goto end;
	}

	if (avformat_find_stream_info(fmt_ctx, NULL) < 0) {
		/*fprintf(stderr, "Could not find stream information\n");
		exit(1);*/
		error = "Could not find stream information\n";
		ret = 1;
		goto end;
	}

	open_codec_context(fmt_ctx, AVMEDIA_TYPE_VIDEO, &unit, &framerate);
	cout << "time unit: " << unit << endl;
	cout << "framerate: " << framerate << endl;
	
	av_dump_format(fmt_ctx, 0, src_filename, 0);

	if (!video_stream) {
		error = "Could not find video stream in the input, aborting";
		ret = 1;
		goto end;
	}

	frame = av_frame_alloc();
	if (!frame) {
		error = "Could not allocate frame";
		ret = AVERROR(ENOMEM);
		goto end;
	}
	
	printf("framenum,source,blockw,blockh,srcx,srcy,dstx,dsty,flags\n");

	// Output file is in AppData\Local\Packages\<package id>\LocalCache directory
	// open file 
	ofile.open(dst_filename);

	/* read frames from the file */
	while (av_read_frame(fmt_ctx, &pkt) >= 0) {
		if (pkt.stream_index == video_stream_idx) {
			int64_t pts, dts;
			pts = pkt.pts;
			dts = pkt.dts;
			pts_array[totalframes++] = pts;
		}
	}

	sort(pts_array, pts_array + totalframes);
	ppts = 0;
	for (int i = 0; i < totalframes; i++) {
		ofile << pts_array[i] * unit << endl;
		//cout << "pts " << pts_array[i] << " pts time " << pts_array[i] * unit << " duration " << (pts_array[i] - ppts) * unit << endl;
		//ppts = pts_array[i];
	}

	ofile.close();

	printf("\n[done]\n");
	if (ret == 0) // everything was successful
	{
		// Create the success file 
		successFileName = folderPathStr + "\\success.txt";
		successFile.open(successFileName);
		successFile << "success";
		successFile.close();
	}
	/* flush cached frames */
end:
	avcodec_free_context(&video_dec_ctx);
	avformat_close_input(&fmt_ctx);
	av_frame_free(&frame);
	if (ret != 0)
	{
		auto hstringError = winrt::to_hstring(error);
		appDataValues.Insert(L"errorMsg", winrt::box_value(hstringError));
	}
	return ret < 0;
}

